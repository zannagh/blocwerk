// <copyright file="WallBatchSaveLatchTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The hold editor's batch save behind one latch: the flush arriving while "Save Changes" is still in flight (or the
/// other way round) must not send the batch twice, which would add its new holds again. Driven through the component
/// itself, without a renderer (there is no bUnit here), so the private save is reached by reflection.
/// </summary>
public class WallBatchSaveLatchTests
{
    private static readonly MethodInfo SaveBatch =
        typeof(WallPhotoEditor).GetMethod("SaveBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task ASecondSaveWhileTheFirstIsInFlightSendsNothing()
    {
        var gate = new TaskCompletionSource();
        var sends = new List<WallPhotoEditor.BatchChanges>();
        var editor = EditorWithOneAddedHold(async changes =>
        {
            sends.Add(changes);
            await gate.Task;
        });

        var first = Save(editor, includeDestructive: true);
        var second = await Save(editor, includeDestructive: true);
        gate.SetResult();

        Assert.True(await first);
        Assert.False(second);
        Assert.Single(sends);
        Assert.Single(sends[0].Added);
        Assert.Empty(Field<List<Hold>>(editor, "_addedHolds"));
    }

    [Fact]
    public async Task AHoldAddedWhileTheSaveIsInFlightStaysPending()
    {
        var gate = new TaskCompletionSource();
        WallPhotoEditor? editorRef = null;
        var late = new Hold { WallId = Guid.NewGuid(), X = 0.2, Y = 0.2, Radius = 0.02 };
        var editor = EditorWithOneAddedHold(async _ =>
        {
            Field<List<Hold>>(editorRef!, "_addedHolds").Add(late);
            await gate.Task;
        });
        editorRef = editor;

        var save = Save(editor, includeDestructive: true);
        gate.SetResult();
        await save;

        Assert.Equal([late], Field<List<Hold>>(editor, "_addedHolds"));
    }

    /// <summary>The adds-and-moves-only scope leaves deletions and "changed" marks pending for the device copy.</summary>
    [Fact]
    public async Task TheAddsAndMovesOnlyScopeLeavesDestructiveDecisionsPending()
    {
        WallPhotoEditor.BatchChanges? sent = null;
        var editor = EditorWithOneAddedHold(changes =>
        {
            sent = changes;
            return Task.CompletedTask;
        });
        var deleted = Guid.NewGuid();
        var marked = Guid.NewGuid();
        Field<HashSet<Guid>>(editor, "_deletedHoldIds").Add(deleted);
        Field<HashSet<Guid>>(editor, "_markChangedHoldIds").Add(marked);

        Assert.True(await Save(editor, includeDestructive: false));

        Assert.Empty(sent!.Deleted);
        Assert.Empty(sent.MarkedChanged);
        Assert.Single(sent.Added);
        Assert.Equal([deleted], Field<HashSet<Guid>>(editor, "_deletedHoldIds"));
        Assert.Equal([marked], Field<HashSet<Guid>>(editor, "_markChangedHoldIds"));
    }

    private static WallPhotoEditor EditorWithOneAddedHold(Func<WallPhotoEditor.BatchChanges, Task> onSave)
    {
        // Set directly because there is no renderer here to pass parameters (BL0005 is about real renders).
#pragma warning disable BL0005
        var editor = new WallPhotoEditor
        {
            OnBatchSave = new EventCallback<WallPhotoEditor.BatchChanges>(null, onSave),
        };
#pragma warning restore BL0005
        var hold = new Hold { WallId = Guid.NewGuid(), X = 0.5, Y = 0.5, Radius = 0.02 };
        editor.Holds.Add(hold);
        Field<List<Hold>>(editor, "_addedHolds").Add(hold);
        return editor;
    }

    private static Task<bool> Save(WallPhotoEditor editor, bool includeDestructive) =>
        (Task<bool>)SaveBatch.Invoke(editor, [includeDestructive])!;

    private static T Field<T>(WallPhotoEditor editor, string name) =>
        (T)typeof(WallPhotoEditor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
}

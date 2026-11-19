// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>The boulder list shows a family of variants as its parent's row (<see cref="BoulderFamilies"/>).</summary>
public partial class WallDetail
{
    private static readonly BoulderFamilyRow NoFamily = new(0, null, 0, [], []);

    private IReadOnlyDictionary<Guid, BoulderFamilyRow>? familyRowsCache;
    private Dictionary<Guid, BoulderListItem> familyItemsById = [];
    private object? familyRowsItemsRef;
    private object? familyRowsHoldsRef;

    // Recomputed when _items is reassigned, which is the only way the list changes, or the holds (their types) are.
    private IReadOnlyDictionary<Guid, BoulderFamilyRow> FamilyRows
    {
        get
        {
            if (familyRowsCache is null || !ReferenceEquals(familyRowsItemsRef, _items) || !ReferenceEquals(familyRowsHoldsRef, _holds))
            {
                (familyRowsItemsRef, familyRowsHoldsRef) = (_items, _holds);
                familyRowsCache = BoulderFamilies.Rows(_items.Select(i => FamilyMember(i.Boulder)));
                familyItemsById = _items.ToDictionary(i => i.Boulder.Id);
            }

            return familyRowsCache;
        }
    }

    private BoulderFamilyMember FamilyMember(Boulder b) =>
        new(b.Id, b.ParentBoulderId, b.Name, b.Grade, b.IsDraft, b.IsArchived, b.Styles, b.IsHistoric, EffectiveHoldTypeOf(b)?.Types ?? BoulderHoldType.None);

    private BoulderFamilyRow FamilyOf(BoulderListItem item) => FamilyRows.GetValueOrDefault(item.Boulder.Id, NoFamily);

    // A filter on the viewer's marks or the review flag keeps a family when any of its published members passes.
    // FamilyOf refreshes the lookup with the rows, so it is read first.
    private bool AnyMember(BoulderListItem item, Func<BoulderListItem, bool> test)
    {
        var row = FamilyOf(item);
        return BoulderFamilies.AnyMember(item, row, id => familyItemsById.GetValueOrDefault(id), test);
    }
}

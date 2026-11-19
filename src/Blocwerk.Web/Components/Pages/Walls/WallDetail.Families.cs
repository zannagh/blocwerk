// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>The boulder list shows a family of variants as its parent's row (<see cref="BoulderFamilies"/>).</summary>
public partial class WallDetail
{
    private static readonly BoulderFamilyRow NoFamily = new(0, null, 0, [], []);

    private IReadOnlyDictionary<Guid, BoulderFamilyRow>? familyRowsCache;
    private object? familyRowsItemsRef;

    // Recomputed when _items is reassigned, which is the only way the list changes.
    private IReadOnlyDictionary<Guid, BoulderFamilyRow> FamilyRows
    {
        get
        {
            if (familyRowsCache is null || !ReferenceEquals(familyRowsItemsRef, _items))
            {
                familyRowsItemsRef = _items;
                familyRowsCache = BoulderFamilies.Rows(_items.Select(i => FamilyMember(i.Boulder)));
            }

            return familyRowsCache;
        }
    }

    private static BoulderFamilyMember FamilyMember(Boulder b) =>
        new(b.Id, b.ParentBoulderId, b.Name, b.Grade, b.IsDraft, b.IsArchived, b.Styles, b.IsHistoric);

    private BoulderFamilyRow FamilyOf(BoulderListItem item) => FamilyRows.GetValueOrDefault(item.Boulder.Id, NoFamily);
}

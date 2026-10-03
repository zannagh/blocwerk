// <copyright file="WallPanelCrop.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// The uncropped original of a cropped live panel photo, so the crop can be undone. One row per cropped panel (the panel
/// id is the key): a second crop of the same panel is cut from <see cref="OriginalPhoto"/> again and only moves the
/// rectangle, so cropping twice never compounds JPEG loss. Removed by "Undo crop", which writes the original back to
/// <see cref="WallPanel.Photo"/>. Kept in its own table so loading a panel row never drags a second multi-megabyte blob.
/// </summary>
public class WallPanelCrop
{
    /// <summary>The cropped panel (also the key).</summary>
    [Key]
    public Guid WallPanelId { get; set; }

    [ForeignKey(nameof(WallPanelId))]
    public WallPanel WallPanel { get; set; } = null!;

    /// <summary>The panel photo exactly as it was before the first crop.</summary>
    [Required]
    public byte[] OriginalPhoto { get; set; } = [];

    [MaxLength(64)]
    public string? OriginalPhotoContentType { get; set; }

    /// <summary>Left edge of the current crop, as a fraction (0..1) of the original's width.</summary>
    public double Left { get; set; }

    /// <summary>Top edge of the current crop, as a fraction (0..1) of the original's height.</summary>
    public double Top { get; set; }

    /// <summary>Width of the current crop, as a fraction (0..1] of the original's width.</summary>
    public double Width { get; set; }

    /// <summary>Height of the current crop, as a fraction (0..1] of the original's height.</summary>
    public double Height { get; set; }

    public DateTimeOffset CroppedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? CroppedByUserId { get; set; }
}

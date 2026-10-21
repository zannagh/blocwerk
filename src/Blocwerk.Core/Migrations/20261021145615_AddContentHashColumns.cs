using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddContentHashColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "PhotoSha256",
                table: "Walls",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "StagedPhotoSha256",
                table: "Walls",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PreviousPhotoSha256",
                table: "WallResets",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PhotoSha256",
                table: "WallPanels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "StagedPhotoSha256",
                table: "WallPanels",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Sha256",
                table: "WallImages",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "MaskSha256",
                table: "WallGeometryTextures",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Sha256",
                table: "WallGeometryTextures",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "MobileSha256",
                table: "WallGeometrySplats",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Sha256",
                table: "WallGeometrySplats",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "AvatarSha256",
                table: "Users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ThumbnailSha256",
                table: "BetaVideos",
                type: "bytea",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-4000-8000-000000000001"),
                column: "AvatarSha256",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PhotoSha256",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoSha256",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PreviousPhotoSha256",
                table: "WallResets");

            migrationBuilder.DropColumn(
                name: "PhotoSha256",
                table: "WallPanels");

            migrationBuilder.DropColumn(
                name: "StagedPhotoSha256",
                table: "WallPanels");

            migrationBuilder.DropColumn(
                name: "Sha256",
                table: "WallImages");

            migrationBuilder.DropColumn(
                name: "MaskSha256",
                table: "WallGeometryTextures");

            migrationBuilder.DropColumn(
                name: "Sha256",
                table: "WallGeometryTextures");

            migrationBuilder.DropColumn(
                name: "MobileSha256",
                table: "WallGeometrySplats");

            migrationBuilder.DropColumn(
                name: "Sha256",
                table: "WallGeometrySplats");

            migrationBuilder.DropColumn(
                name: "AvatarSha256",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ThumbnailSha256",
                table: "BetaVideos");
        }
    }
}

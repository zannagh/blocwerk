// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.OfflineClient;

/// <summary>
/// The 3D view's files the offline client's shell names (PR 3.13; plan §4: everything a view loads is named by the
/// shell): Web's own <c>js/wall3d.js</c> and every module it imports, statically or on demand (Spark only for
/// photo-real), with the vendored three.js and Spark, and wall3d.js's stylesheet. The modules go into the shell's import
/// map, fingerprinted, so the worker precaches them with the client and the version hashes them, while nothing loads
/// them before a 3D view opens (client-3d-stage.js imports <c>/js/wall3d.js</c> on the first mount). Spark's decode
/// worker and its WebAssembly are inline in the module (a blob), so they are no files of their own. A Core test pins
/// the list to wall3d.js's import graph.
/// </summary>
public static class OfflineClient3DAssets
{
    /// <summary>wall3d.js's stylesheet: the shell links it, so wall3d-stage.js injects no copy by its plain path.</summary>
    public const string Stylesheet = "css/wall3d.css";

    /// <summary>The 3D view's modules by static-asset label, wall3d.js first.</summary>
    public static readonly string[] Modules =
    [
        "js/wall3d.js",
        "js/wall3d-body-clip.js",
        "js/wall3d-body.js",
        "js/wall3d-camera.js",
        "js/wall3d-clearance.js",
        "js/wall3d-doc.js",
        "js/wall3d-focus.js",
        "js/wall3d-ghost.js",
        "js/wall3d-glow.js",
        "js/wall3d-hold-nav.js",
        "js/wall3d-holds.js",
        "js/wall3d-keys.js",
        "js/wall3d-locate.js",
        "js/wall3d-loop.js",
        "js/wall3d-modes.js",
        "js/wall3d-occlude.js",
        "js/wall3d-outlines.js",
        "js/wall3d-overlay.js",
        "js/wall3d-photos.js",
        "js/wall3d-pick.js",
        "js/wall3d-reach.js",
        "js/wall3d-relief.js",
        "js/wall3d-scene.js",
        "js/wall3d-sides.js",
        "js/wall3d-splat-clip.js",
        "js/wall3d-splat-cull.js",
        "js/wall3d-splat-detail.js",
        "js/wall3d-splat-diag.js",
        "js/wall3d-splat-fetch.js",
        "js/wall3d-splat-hold.js",
        "js/wall3d-splat-ladder.js",
        "js/wall3d-splat-levels.js",
        "js/wall3d-splat-policy.js",
        "js/wall3d-splat-recover.js",
        "js/wall3d-splat-safe.js",
        "js/wall3d-splat-store.js",
        "js/wall3d-splat.js",
        "js/wall3d-stage.js",
        "js/wall3d-ui.js",
        "js/wall3d-volumes.js",
        "lib/three/three.module.min.js",
        "lib/three/three.core.min.js",
        "lib/three/controls/OrbitControls.js",
        "lib/three/postprocessing/Pass.js",
        "lib/spark/spark.module.min.js",
    ];
}

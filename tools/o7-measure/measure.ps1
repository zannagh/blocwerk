# Runs every case in its own throwaway wall-geometry container (no network, 2 CPUs like prod) and prints one JSON line
# per case (replay.py). Never touches running containers. On the experimental PC:
#   pwsh measure.ps1 -Image blocwerk-exp/wall-geometry:<tag> -Data <dir with req\ sfm\ and these scripts> `
#        -Photos <wall-images\captures> [-Memory 1536m] [-BlendBytes 2000000000] [-Cases "solve,textures:high"] [-Req req]
param(
    [Parameter(Mandatory)] [string] $Image,
    [Parameter(Mandatory)] [string] $Data,
    [Parameter(Mandatory)] [string] $Photos,
    [string] $Memory = "",
    [string] $BlendBytes = "2000000000",
    [string] $JobMaxMemoryMb = "",
    [string] $Tag = "run",
    [string] $Req = "req",
    # a computejobs/ directory mounted over the image's (to try a guard change without building an image)
    [string] $Computejobs = "",
    [string] $Cases = "solve,solve-sfm,textures:standard,textures:high,textures:maximum"
)
foreach ($case in $Cases.Split(",")) {
    $kind, $quality = $case.Split(":")
    $name = "o7m-$Tag-" + ($case -replace ":", "-")
    $limits = if ($Memory) { @("--memory=$Memory", "--memory-swap=$Memory") } else { @() }
    if ($Computejobs) { $limits += @("-v", "${Computejobs}:/app/computejobs:ro") }
    docker rm -f $name 2>$null | Out-Null
    docker run -d --name $name --network none --cpus 2 @limits `
        -e HOST=127.0.0.1 -e CAPTURE_MAX_PHOTOS=1000 -e TEXTURES_BLEND_MAX_BYTES=$BlendBytes -e JOB_MAX_MEMORY_MB=$JobMaxMemoryMb `
        -v "${Data}:/measure:ro" -v "${Photos}:/photos:ro" $Image | Out-Null
    $replayArgs = switch ($kind) {
        "solve" { @("solve", "/measure/$Req") }
        "solve-sfm" { @("solve-sfm", "/measure/sfm") }
        default { @("textures", "/measure/$Req", "/photos", $quality) }
    }
    $line = docker exec $name python /measure/replay.py @replayArgs 2>&1 | Select-Object -Last 1
    $state = docker inspect $name --format "{{.State.OOMKilled}} {{.State.Status}}"
    Write-Output "$line memory=$Memory blend=$BlendBytes container=[$state]"
    docker rm -f $name | Out-Null
}

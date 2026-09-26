// Blazor side of the 3D view's surface taps (Wall3DStage.razor.cs): every tap reports the facet under it to .NET,
// for the model corrections ("This surface is vertical", "Not part of the wall"). Returns the unsubscribe function.
export function listenFacetTaps(handle, dotnet) {
    return handle.onFacetTap(facetId => {
        dotnet.invokeMethodAsync('FacetTapped', facetId ?? null).catch(() => { /* the circuit went away */ });
    });
}

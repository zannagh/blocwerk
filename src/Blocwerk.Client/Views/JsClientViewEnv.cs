// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Microsoft.JSInterop;

namespace Blocwerk.Client.Views;

/// <summary>
/// The view facts through client-view.js (<c>window.bwClientView</c>), read once per start: a rotation keeps the rung
/// the page began with, and a miss on it falls back to the thumbnail (<see cref="PhotoUrls"/>). Offsets travel as
/// comma-separated numbers, so no reflection serializer touches them.
/// </summary>
public sealed class JsClientViewEnv : IClientViewEnv
{
    private const int MaxOffsetMinutes = 14 * 60;

    private readonly IJSRuntime js;
    private Task<ClientViewFacts>? facts;

    /// <summary>Initializes a new instance of the <see cref="JsClientViewEnv"/> class.</summary>
    /// <param name="js">The browser's JS runtime.</param>
    public JsClientViewEnv(IJSRuntime js)
    {
        this.js = js;
    }

    /// <inheritdoc />
    public Task<ClientViewFacts> FactsAsync() => facts ??= ReadAsync();

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimeSpan>> OffsetsAsync(IReadOnlyList<DateTimeOffset> moments)
    {
        var today = (await FactsAsync()).UtcOffset;
        if (moments.Count == 0)
        {
            return [];
        }

        try
        {
            var asked = string.Join(',', moments.Select(m => m.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
            var answer = (await js.InvokeAsync<string>("bwClientView.tzOffsets", asked)).Split(',');
            return moments.Select((_, i) => i < answer.Length && int.TryParse(answer[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                ? Minutes(m)
                : today).ToList();
        }
        catch (JSException)
        {
            return moments.Select(_ => today).ToList();
        }
    }

    private static TimeSpan Minutes(int minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, -MaxOffsetMinutes, MaxOffsetMinutes));

    private async Task<ClientViewFacts> ReadAsync()
    {
        try
        {
            var width = await js.InvokeAsync<int>("bwClientView.fitWidth");
            var offset = await js.InvokeAsync<int>("bwClientView.tzOffset");
            return new ClientViewFacts(width > 0 ? width : ClientViewFacts.Default.FitWidth, Minutes(offset));
        }
        catch (JSException)
        {
            return ClientViewFacts.Default;
        }
    }
}

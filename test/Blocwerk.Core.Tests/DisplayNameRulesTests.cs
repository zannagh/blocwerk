// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="DisplayNameRules"/>: climbers' emoji, joined ones included, are names; bidi controls and stray invisible
/// characters are not.
/// </summary>
public sealed class DisplayNameRulesTests
{
    [Theory]
    [InlineData("Zoë")]
    [InlineData("Crimp Queen \U0001F9D7‍♀️")]
    [InlineData("\U0001F9D7\U0001F3FD‍♂️ Sam")]
    [InlineData("Fam \U0001F468‍\U0001F469‍\U0001F467")]
    [InlineData("עידו")]
    public void ANameWithEmojiOrAnotherScript_IsFine(string name) =>
        Assert.False(DisplayNameRules.HasInvisibleCharacters(name));

    [Theory]
    [InlineData("Ad‮min")]
    [InlineData("Sam⁦x⁩")]
    [InlineData("Sam​")]
    [InlineData("Sam‌")]
    [InlineData("﻿Sam")]
    [InlineData("Sam‎")]
    [InlineData("Sam‍bo")]
    [InlineData("‍\U0001F9D7")]
    [InlineData("\U0001F9D7‍")]
    public void BidiControlsAndStrayInvisibles_AreRefused(string name) =>
        Assert.True(DisplayNameRules.HasInvisibleCharacters(name));
}

// <copyright file="PartyAutoCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu: chat command to toggle automatic party-request response. The mode is stored as the
/// runtime <see cref="Stats.PartyAutoMode"/> attribute (0 = normal/manual popup, 1 = auto-accept,
/// 2 = auto-decline) via the existing <c>SetStatAttribute</c> mechanism, and consumed by the party
/// request flow. Runtime-only: the mode is not persisted and resets on relog. The default (0)
/// leaves the normal manual popup behavior unchanged.
/// /re auto -> auto-accept; /re off -> auto-decline; /re (or anything else) -> normal/manual.
/// </summary>
[Guid("A3F2C1D4-8B6E-4F9A-B2D7-5C3E1A8F0D62")]
[PlugIn]
[Display(Name = "Party Auto Command", Description = "BarnaMu: toggle automatic party-request response. Usage: /re [auto|off] (no argument resets to normal/manual).")]
[ChatCommandHelp(Command, typeof(Arguments), MinimumStatus)]
public class PartyAutoCommandPlugIn : ChatCommandPlugInBase<PartyAutoCommandPlugIn.Arguments>
{
    private const string Command = "/re";
    private const CharacterStatus MinimumStatus = CharacterStatus.Normal;

    /// <inheritdoc />
    public override string Key => Command;

    /// <inheritdoc />
    public override CharacterStatus MinCharacterStatusRequirement => MinimumStatus;

    /// <inheritdoc />
    protected override async ValueTask DoHandleCommandAsync(Player player, Arguments arguments)
    {
        switch (arguments.Mode?.ToLowerInvariant())
        {
            case "auto":
                player.Attributes?.SetStatAttribute(Stats.PartyAutoMode, 1f);
                await player.ShowBlueMessageAsync("Party auto-accept: ON").ConfigureAwait(false);
                break;
            case "off":
                player.Attributes?.SetStatAttribute(Stats.PartyAutoMode, 2f);
                await player.ShowBlueMessageAsync("Party auto-decline: ON").ConfigureAwait(false);
                break;
            default:
                player.Attributes?.SetStatAttribute(Stats.PartyAutoMode, 0f);
                await player.ShowBlueMessageAsync("Party auto mode: OFF (normal)").ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Arguments for the <c>/re</c> command.
    /// </summary>
    public class Arguments : ArgumentsBase
    {
        /// <summary>
        /// Gets or sets the mode: "auto", "off", or empty to reset to normal.
        /// </summary>
        public string? Mode { get; set; }
    }
}

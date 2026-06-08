// <copyright file="DuelLadderRequestHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.MuHelper;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// BarnaMu: handler for the in-game Duel Ladder window's requests
/// (0xBF group, sub-code 0x32). Op byte at index 4:
/// 0 = request top-10 for bracket (arg at [5]: bracket id 1-5),
/// 1 = request own profile (no further args),
/// 2 = request waiting-to-fight list,
/// 3 = list me as waiting,
/// 4 = remove me from waiting,
/// 5 = challenge waiting player at index (arg at [5]).
/// </summary>
[PlugIn]
[Display(Name = nameof(DuelLadderRequestHandlerPlugIn), Description = "BarnaMu: handles Duel Ladder top-10 and profile queries from the client.")]
[Guid("D5E30CF3-AAAA-4A3E-9F56-C3D4E5F6A7B8")]
[BelongsToGroup(MuHelperGroupHandler.GroupKey)]
public class DuelLadderRequestHandlerPlugIn : ISubPacketHandlerPlugIn
{
    /// <summary>
    /// Sub-code of the Duel Ladder packet within the MU Helper (0xBF) group.
    /// </summary>
    internal const byte SubCode = 0x32;

    private readonly DuelLadderQueryAction _action = new();

    private readonly DuelLadderWaitingAction _waitingAction = new();

    /// <inheritdoc />
    public bool IsEncryptionExpected => false;

    /// <inheritdoc />
    public byte Key => SubCode;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length < 5)
        {
            return;
        }

        var span = packet.Span;
        switch (span[4])
        {
            case 0:
                if (packet.Length < 6)
                {
                    return;
                }

                await this._action.QueryTopAsync(player, span[5]).ConfigureAwait(false);
                break;
            case 1:
                await this._action.QueryProfileAsync(player).ConfigureAwait(false);
                break;
            case 2: // request waiting-to-fight list
                await this._waitingAction.QueryWaitingAsync(player).ConfigureAwait(false);
                break;
            case 3: // list me as waiting
                await this._waitingAction.ListMeAsync(player).ConfigureAwait(false);
                break;
            case 4: // remove me from waiting
                await this._waitingAction.RemoveMeAsync(player).ConfigureAwait(false);
                break;
            case 5: // challenge waiting player at index span[5]
                if (packet.Length < 6)
                {
                    return;
                }

                await this._waitingAction.ChallengeWaitingAsync(player, span[5]).ConfigureAwait(false);
                break;
            case 8: // request match history
                await this._action.QueryHistoryAsync(player).ConfigureAwait(false);
                break;
            case 10: // request hall of fame for a bracket (arg at [5]: 1-5, or 0 for all)
                await this._action.QueryHallOfFameAsync(player, packet.Length > 5 ? span[5] : (byte)0).ConfigureAwait(false);
                break;
            case 11: // challenge ranking player; arg = (bracket << 5) | rowIndex
                if (packet.Length < 6)
                {
                    return;
                }

                await this._action.ChallengeRankAsync(player, (byte)(span[5] >> 5), (byte)(span[5] & 0x1F)).ConfigureAwait(false);
                break;
            default:
                break;
        }
    }
}

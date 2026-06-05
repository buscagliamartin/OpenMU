// <copyright file="IMuHelperConfigurationUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Views.MuHelper;

/// <summary>
/// View plugin which sends saved Mu Helper configuration data to the client.
/// </summary>
public interface IMuHelperConfigurationUpdatePlugIn : IViewPlugIn
{
    /// <summary>
    /// Sends the raw 257-byte Mu Helper configuration blob.
    /// </summary>
    /// <param name="data">The raw configuration data.</param>
    ValueTask UpdateMuHelperConfigurationAsync(Memory<byte> data);
}

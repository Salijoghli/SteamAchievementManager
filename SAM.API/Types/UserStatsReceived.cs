/* Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System.Runtime.InteropServices;

namespace SAM.API.Types
{
    // Steamworks UserStatsReceived_t (callback 1101):
    //   uint64 m_nGameID; EResult m_eResult; CSteamID m_steamIDUser;
    // Windows builds of the Steam API pack callback structs to 8 bytes, so m_steamIDUser
    // (a 64-bit CSteamID) sits at offset 16, not 12: GameId @0, Result @8, SteamIdUser @16,
    // 24 bytes in total. Pack = 1 would read SteamIdUser from the padding at offset 12.
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct UserStatsReceived
    {
        public ulong GameId;
        public int Result;
        public ulong SteamIdUser;
    }
}

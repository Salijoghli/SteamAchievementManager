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

using System;
using System.Runtime.InteropServices;

namespace SAM.API.Interfaces
{
    /// <summary>
    /// The start of the "SteamFriends017" vtable, in the order of isteamfriends.h from
    /// Steamworks SDK 1.61 (the last SDK with STEAMFRIENDS_INTERFACE_VERSION "SteamFriends017").
    /// Only the entries up to the last one used are declared.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ISteamFriends017
    {
        public IntPtr GetPersonaName; // 0
        public IntPtr SetPersonaName; // 1
        public IntPtr GetPersonaState; // 2
        public IntPtr GetFriendCount; // 3
        public IntPtr GetFriendByIndex; // 4
        public IntPtr GetFriendRelationship; // 5
        public IntPtr GetFriendPersonaState; // 6
        public IntPtr GetFriendPersonaName; // 7
    }
}

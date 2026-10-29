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
    /// The start of the "SteamFriends018" vtable, in the order of isteamfriends.h from
    /// Steamworks SDK 1.62+. Unlike 017 it has no SetPersonaName, so every later entry
    /// moves up by one. Only the entries up to the last one used are declared.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ISteamFriends018
    {
        public IntPtr GetPersonaName; // 0
        public IntPtr GetPersonaState; // 1
        public IntPtr GetFriendCount; // 2
        public IntPtr GetFriendByIndex; // 3
        public IntPtr GetFriendRelationship; // 4
        public IntPtr GetFriendPersonaState; // 5
        public IntPtr GetFriendPersonaName; // 6
    }
}

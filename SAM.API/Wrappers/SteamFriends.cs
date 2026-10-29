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
using SAM.API.Interfaces;

namespace SAM.API.Wrappers
{
    /// <summary>The read-only friend-list calls shared by the ISteamFriends versions SAM uses.</summary>
    public interface ISteamFriendList
    {
        /// <summary>The interface version string this wrapper was created with.</summary>
        string Version { get; }

        /// <summary>Number of users matching <paramref name="friendFlags"/> (k_EFriendFlag*).</summary>
        int GetFriendCount(int friendFlags);

        /// <summary>SteamID64 of the user at <paramref name="index"/>; same flags as <see cref="GetFriendCount"/>.</summary>
        ulong GetFriendByIndex(int index, int friendFlags);

        /// <summary>EPersonaState of a friend; 0 (k_EPersonaStateOffline) when offline.</summary>
        int GetFriendPersonaState(ulong steamId);

        /// <summary>The friend's persona name (UTF-8 from Steam); never null.</summary>
        string GetFriendPersonaName(ulong steamId);
    }

    /// <summary>
    /// Native signatures live outside the generic wrapper: delegate types nested in a generic
    /// class are generic, and Marshal.GetDelegateForFunctionPointer rejects generic types.
    /// </summary>
    internal static class SteamFriendsNative
    {
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        internal delegate int NativeGetFriendCount(IntPtr self, int friendFlags);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        internal delegate void NativeGetFriendByIndex(IntPtr self, out ulong steamId, int index, int friendFlags);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        internal delegate int NativeGetFriendPersonaState(IntPtr self, ulong steamId);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        internal delegate IntPtr NativeGetFriendPersonaName(IntPtr self, ulong steamId);
    }

    /// <summary>
    /// Shared implementation. On x64 every method is a plain MSVC member call: <c>this</c> comes
    /// first, a CSteamID argument (an 8-byte struct) is passed by value in a register like a
    /// uint64, and a CSteamID return value goes through a hidden pointer that MSVC passes right
    /// after <c>this</c> for member functions (same as <see cref="SteamUser012.GetSteamId"/>).
    /// </summary>
    public abstract class SteamFriendsBase<TNative> : NativeWrapper<TNative>, ISteamFriendList
    {
        public abstract string Version { get; }

        protected abstract IntPtr GetFriendCountPointer { get; }

        protected abstract IntPtr GetFriendByIndexPointer { get; }

        protected abstract IntPtr GetFriendPersonaStatePointer { get; }

        protected abstract IntPtr GetFriendPersonaNamePointer { get; }

        public int GetFriendCount(int friendFlags)
        {
            var call = this.GetFunction<SteamFriendsNative.NativeGetFriendCount>(this.GetFriendCountPointer);
            return call(this.ObjectAddress, friendFlags);
        }

        public ulong GetFriendByIndex(int index, int friendFlags)
        {
            var call = this.GetFunction<SteamFriendsNative.NativeGetFriendByIndex>(this.GetFriendByIndexPointer);
            call(this.ObjectAddress, out var steamId, index, friendFlags);
            return steamId;
        }

        public int GetFriendPersonaState(ulong steamId)
        {
            var call = this.GetFunction<SteamFriendsNative.NativeGetFriendPersonaState>(this.GetFriendPersonaStatePointer);
            return call(this.ObjectAddress, steamId);
        }

        public string GetFriendPersonaName(ulong steamId)
        {
            var call = this.GetFunction<SteamFriendsNative.NativeGetFriendPersonaName>(this.GetFriendPersonaNamePointer);
            return NativeStrings.PointerToString(call(this.ObjectAddress, steamId)) ?? string.Empty;
        }
    }

    /// <summary>ISteamFriends "SteamFriends017" (Steamworks SDK 1.61 and earlier).</summary>
    public class SteamFriends017 : SteamFriendsBase<ISteamFriends017>
    {
        public const string InterfaceVersion = "SteamFriends017";

        public override string Version => InterfaceVersion;

        protected override IntPtr GetFriendCountPointer => this.Functions.GetFriendCount;

        protected override IntPtr GetFriendByIndexPointer => this.Functions.GetFriendByIndex;

        protected override IntPtr GetFriendPersonaStatePointer => this.Functions.GetFriendPersonaState;

        protected override IntPtr GetFriendPersonaNamePointer => this.Functions.GetFriendPersonaName;
    }

    /// <summary>ISteamFriends "SteamFriends018" (Steamworks SDK 1.62+, no SetPersonaName).</summary>
    public class SteamFriends018 : SteamFriendsBase<ISteamFriends018>
    {
        public const string InterfaceVersion = "SteamFriends018";

        public override string Version => InterfaceVersion;

        protected override IntPtr GetFriendCountPointer => this.Functions.GetFriendCount;

        protected override IntPtr GetFriendByIndexPointer => this.Functions.GetFriendByIndex;

        protected override IntPtr GetFriendPersonaStatePointer => this.Functions.GetFriendPersonaState;

        protected override IntPtr GetFriendPersonaNamePointer => this.Functions.GetFriendPersonaName;
    }
}

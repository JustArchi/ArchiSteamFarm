﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2016 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using System;
using System.Reflection;
using Microsoft.Win32;

namespace ArchiSteamFarm {
	internal static class Runtime {
		private static readonly Type MonoRuntime = Type.GetType("Mono.Runtime");

		internal static bool IsRunningOnMono => MonoRuntime != null;

		private static bool? _IsUserInteractive;
		internal static bool IsUserInteractive {
			get {
				if (_IsUserInteractive.HasValue) {
					return _IsUserInteractive.Value;
				}

				if (Environment.UserInteractive) {
					_IsUserInteractive = true;
				} else if (!IsRunningOnMono) {
					// If it's non-Mono, we can trust the result
					_IsUserInteractive = false;
				} else {
					// In Mono, Environment.UserInteractive is always false
					// There is really no reliable way for now, so assume always being interactive
					// Maybe in future I find out some awful hack or workaround that could be at least semi-reliable
					_IsUserInteractive = true;
				}

				return _IsUserInteractive.Value;
			}
		}

		private static bool? _IsRuntimeSupported;
		internal static bool IsRuntimeSupported {
			get {
				if (_IsRuntimeSupported.HasValue) {
					return _IsRuntimeSupported.Value;
				}

				if (IsRunningOnMono) {
					Version monoVersion = GetMonoVersion();
					if (monoVersion == null) {
						Logging.LogNullError(nameof(monoVersion));
						return false;
					}

					Version minMonoVersion = new Version(4, 4);

					if (monoVersion >= minMonoVersion) {
						Logging.LogGenericInfo("Your Mono version is OK. Required: " + minMonoVersion + " | Found: " + monoVersion);
						_IsRuntimeSupported = true;
						return true;
					}

					Logging.LogGenericWarning("Your Mono version is too old. Required: " + minMonoVersion + " | Found: " + monoVersion);
					_IsRuntimeSupported = false;
					return false;
				}

				Version netVersion = GetNetVersion();
				if (netVersion == null) {
					Logging.LogNullError(nameof(netVersion));
					return false;
				}

				Version minNetVersion = new Version(4, 6, 1);

				if (netVersion >= minNetVersion) {
					Logging.LogGenericInfo("Your .NET version is OK. Required: " + minNetVersion + " | Found: " + netVersion);
					_IsRuntimeSupported = true;
					return true;
				}

				Logging.LogGenericWarning("Your .NET version is too old. Required: " + minNetVersion + " | Found: " + netVersion);
				_IsRuntimeSupported = false;
				return false;
			}
		}

		// TODO: Remove me once Mono 4.6 is released
		internal static bool RequiresWorkaroundForMonoBug41701() {
			// Mono only, https://bugzilla.xamarin.com/show_bug.cgi?id=41701
			if (!IsRunningOnMono) {
				return false;
			}

			Version monoVersion = GetMonoVersion();

			// The issue affects Mono versions from 4.3.2, 4.4.0 to 4.4.2 (inclusive) and from 4.5.0 to 4.5.2 (inclusive)
			if (monoVersion?.Major != 4) {
				return false;
			}

			switch (monoVersion.Minor) {
				case 3:
					return monoVersion.Build >= 2;
				case 4:
				case 5:
					return monoVersion.Build <= 2;
				default:
					return false;
			}
		}

		// TODO: Remove me once BTLS in Mono 4.8+ is fixed
		internal static bool RequiresNoCertificateValidation() {
			if (!IsRunningOnMono) {
				return false;
			}

			Version monoVersion = GetMonoVersion();

			if (monoVersion?.Major != 4) {
				return false;
			}

			return monoVersion.Minor >= 8;
		}

		private static Version GetNetVersion() {
			uint release;
			using (RegistryKey registryKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey("SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\\")) {
				if (registryKey == null) {
					Logging.LogNullError(nameof(registryKey));
					return null;
				}

				object releaseObj = registryKey.GetValue("Release");
				if (releaseObj == null) {
					Logging.LogNullError(nameof(releaseObj));
					return null;
				}

				if (!uint.TryParse(releaseObj.ToString(), out release) || (release == 0)) {
					Logging.LogNullError(nameof(release));
					return null;
				}
			}

			if (release >= 394747) {
				return new Version(4, 6, 2);
			}

			if (release >= 394254) {
				return new Version(4, 6, 1);
			}

			if (release >= 393295) {
				return new Version(4, 6);
			}

			if (release >= 379893) {
				return new Version(4, 5, 2);
			}

			if (release >= 378675) {
				return new Version(4, 5, 1);
			}

			return release >= 378389 ? new Version(4, 5) : null;
		}

		private static Version GetMonoVersion() {
			if (MonoRuntime == null) {
				Logging.LogNullError(nameof(MonoRuntime));
				return null;
			}

			MethodInfo displayName = MonoRuntime.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static);
			if (displayName == null) {
				Logging.LogNullError(nameof(displayName));
				return null;
			}

			string versionString = (string) displayName.Invoke(null, null);
			if (string.IsNullOrEmpty(versionString)) {
				Logging.LogNullError(nameof(versionString));
				return null;
			}

			int index = versionString.IndexOf(' ');
			if (index <= 0) {
				Logging.LogNullError(nameof(index));
				return null;
			}

			versionString = versionString.Substring(0, index);

			Version version;
			if (Version.TryParse(versionString, out version)) {
				return version;
			}

			Logging.LogNullError(nameof(version));
			return null;
		}
	}
}

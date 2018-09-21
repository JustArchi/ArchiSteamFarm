﻿//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// 
// Copyright 2015-2018 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ArchiSteamFarm.IPC.Requests;
using ArchiSteamFarm.IPC.Responses;
using ArchiSteamFarm.Localization;
using Microsoft.AspNetCore.Mvc;

namespace ArchiSteamFarm.IPC.Controllers.Api {
	[ApiController]
	[Route("Api/ASF")]
	public sealed class ASFController : ControllerBase {
		[HttpGet]
		public ActionResult<GenericResponse<ASFResponse>> ASFGet() {
			uint memoryUsage = (uint) GC.GetTotalMemory(false) / 1024;

			DateTime processStartTime;

			using (Process process = Process.GetCurrentProcess()) {
				processStartTime = process.StartTime;
			}

			ASFResponse result = new ASFResponse(SharedInfo.BuildInfo.Variant, Program.GlobalConfig, memoryUsage, processStartTime, SharedInfo.Version);
			return Ok(new GenericResponse<ASFResponse>(result));
		}

		[HttpPost]
		public async Task<ActionResult<GenericResponse>> ASFPost([FromBody] ASFRequest request) {
			if (request == null) {
				ASF.ArchiLogger.LogNullError(nameof(request));
				return BadRequest(new GenericResponse(false, string.Format(Strings.ErrorIsEmpty, nameof(request))));
			}

			(bool valid, string errorMessage) = request.GlobalConfig.CheckValidation();
			if (!valid) {
				return BadRequest(new GenericResponse(false, errorMessage));
			}

			if (request.KeepSensitiveDetails) {
				if (string.IsNullOrEmpty(request.GlobalConfig.WebProxyPassword) && !string.IsNullOrEmpty(Program.GlobalConfig.WebProxyPassword)) {
					request.GlobalConfig.WebProxyPassword = Program.GlobalConfig.WebProxyPassword;
				}
			}

			request.GlobalConfig.ShouldSerializeEverything = false;

			string filePath = Path.Combine(SharedInfo.ConfigDirectory, SharedInfo.GlobalConfigFileName);

			bool result = await GlobalConfig.Write(filePath, request.GlobalConfig).ConfigureAwait(false);
			return Ok(new GenericResponse(result));
		}

		[HttpPost("Exit")]
		public ActionResult<GenericResponse> ExitPost() {
			(bool success, string output) = Actions.Exit();
			return Ok(new GenericResponse(success, output));
		}

		[HttpPost("Restart")]
		public ActionResult<GenericResponse> RestartPost() {
			(bool success, string output) = Actions.Restart();
			return Ok(new GenericResponse(success, output));
		}

		[HttpPost("Update")]
		public async Task<ActionResult<GenericResponse<Version>>> UpdatePost() {
			(bool success, Version version) = await Actions.Update().ConfigureAwait(false);
			return Ok(new GenericResponse<Version>(success, version));
		}
	}
}

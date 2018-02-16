﻿//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// 
//  Copyright 2015-2018 Łukasz "JustArchi" Domeradzki
//  Contact: JustArchi@JustArchi.net
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//  http://www.apache.org/licenses/LICENSE-2.0
//      
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;
using ArchiSteamFarm.Localization;
using HtmlAgilityPack;
using Newtonsoft.Json;

namespace ArchiSteamFarm {
	internal sealed class WebBrowser : IDisposable {
		internal const byte MaxTries = 5; // Defines maximum number of recommended tries for a single request

		private const byte ExtendedTimeoutMultiplier = 10; // Defines multiplier of timeout for WebBrowsers dealing with huge data (ASF update)
		private const byte MaxConnections = 10; // Defines maximum number of connections per ServicePoint. Be careful, as it also defines maximum number of sockets in CLOSE_WAIT state
		private const byte MaxIdleTime = 15; // Defines in seconds, how long socket is allowed to stay in CLOSE_WAIT state after there are no connections to it

		internal readonly CookieContainer CookieContainer = new CookieContainer();

		internal TimeSpan Timeout => HttpClient.Timeout;

		private readonly ArchiLogger ArchiLogger;
		private readonly HttpClient HttpClient;

		internal WebBrowser(ArchiLogger archiLogger, bool extendedTimeout = false) {
			ArchiLogger = archiLogger ?? throw new ArgumentNullException(nameof(archiLogger));

			HttpClientHandler httpClientHandler = new HttpClientHandler {
				AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip,
				CookieContainer = CookieContainer,
				MaxConnectionsPerServer = MaxConnections,
				UseProxy = false
			};

			HttpClient = new HttpClient(httpClientHandler) { Timeout = TimeSpan.FromSeconds(extendedTimeout ? ExtendedTimeoutMultiplier * Program.GlobalConfig.ConnectionTimeout : Program.GlobalConfig.ConnectionTimeout) };

			// Most web services expect that UserAgent is set, so we declare it globally
			HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(SharedInfo.PublicIdentifier + "/" + SharedInfo.Version);
		}

		public void Dispose() => HttpClient.Dispose();

		internal static void Init() {
			// Set max connection limit from default of 2 to desired value
			ServicePointManager.DefaultConnectionLimit = MaxConnections;

			// Set max idle time from default of 100 seconds (100 * 1000) to desired value
			ServicePointManager.MaxServicePointIdleTime = MaxIdleTime * 1000;

			// Don't use Expect100Continue, we're sure about our POSTs, save some TCP packets
			ServicePointManager.Expect100Continue = false;

			// Reuse ports if possible
			ServicePointManager.ReusePort = true;
		}

		internal static HtmlDocument StringToHtmlDocument(string html) {
			if (string.IsNullOrEmpty(html)) {
				ASF.ArchiLogger.LogNullError(nameof(html));
				return null;
			}

			HtmlDocument htmlDocument = new HtmlDocument();
			htmlDocument.LoadHtml(html);

			return htmlDocument;
		}

		internal async Task<BinaryResponse> UrlGetToBinaryWithProgressRetry(string request, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			BinaryResponse response = null;

			for (byte i = 0; (i < MaxTries) && (response == null); i++) {
				response = await UrlGetToBinaryWithProgress(request, referer).ConfigureAwait(false);
			}

			if (response != null) {
				return response;
			}

			ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorRequestFailedTooManyTimes, MaxTries));
			ArchiLogger.LogGenericDebug(string.Format(Strings.ErrorFailingRequest, request));
			return null;
		}

		internal async Task<HtmlDocumentResponse> UrlGetToHtmlDocumentRetry(string request, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			StringResponse response = await UrlGetToStringRetry(request, referer).ConfigureAwait(false);
			return response != null ? new HtmlDocumentResponse(response) : null;
		}

		internal async Task<ObjectResponse<T>> UrlGetToObjectRetry<T>(string request, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			StringResponse response = await UrlGetToStringRetry(request, referer).ConfigureAwait(false);

			if (response == null) {
				return null;
			}

			T obj;

			try {
				obj = JsonConvert.DeserializeObject<T>(response.Content);
			} catch (JsonException e) {
				ArchiLogger.LogGenericException(e);

				if (Debugging.IsUserDebugging) {
					ArchiLogger.LogGenericDebug(string.Format(Strings.Content, response.Content));
				}

				return null;
			}

			return new ObjectResponse<T>(response, obj);
		}

		internal async Task<XmlResponse> UrlGetToXmlRetry(string request, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			StringResponse response = await UrlGetToStringRetry(request, referer).ConfigureAwait(false);

			if (response == null) {
				return null;
			}

			XmlDocument xmlDocument = new XmlDocument();

			try {
				xmlDocument.LoadXml(response.Content);
			} catch (XmlException e) {
				ArchiLogger.LogGenericException(e);
				return null;
			}

			return new XmlResponse(response, xmlDocument);
		}

		internal async Task<BasicResponse> UrlHeadRetry(string request, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			BasicResponse response = null;

			for (byte i = 0; (i < MaxTries) && (response == null); i++) {
				response = await UrlHead(request, referer).ConfigureAwait(false);
			}

			if (response != null) {
				return response;
			}

			ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorRequestFailedTooManyTimes, MaxTries));
			ArchiLogger.LogGenericDebug(string.Format(Strings.ErrorFailingRequest, request));
			return null;
		}

		internal async Task<BasicResponse> UrlPost(string request, IReadOnlyCollection<KeyValuePair<string, string>> data = null, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			using (HttpResponseMessage response = await UrlPostToHttp(request, data, referer).ConfigureAwait(false)) {
				return response != null ? new BasicResponse(response) : null;
			}
		}

		internal async Task<BasicResponse> UrlPostRetry(string request, IReadOnlyCollection<KeyValuePair<string, string>> data = null, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			BasicResponse response = null;

			for (byte i = 0; (i < MaxTries) && (response == null); i++) {
				response = await UrlPost(request, data, referer).ConfigureAwait(false);
			}

			if (response != null) {
				return response;
			}

			ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorRequestFailedTooManyTimes, MaxTries));
			ArchiLogger.LogGenericDebug(string.Format(Strings.ErrorFailingRequest, request));
			return null;
		}

		internal async Task<HtmlDocumentResponse> UrlPostToHtmlDocumentRetry(string request, IReadOnlyCollection<KeyValuePair<string, string>> data = null, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			StringResponse response = await UrlPostToStringRetry(request, data, referer).ConfigureAwait(false);
			return response != null ? new HtmlDocumentResponse(response) : null;
		}

		internal async Task<ObjectResponse<T>> UrlPostToObjectRetry<T>(string request, IReadOnlyCollection<KeyValuePair<string, string>> data = null, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			StringResponse response = await UrlPostToStringRetry(request, data, referer).ConfigureAwait(false);

			if (response == null) {
				return null;
			}

			T obj;

			try {
				obj = JsonConvert.DeserializeObject<T>(response.Content);
			} catch (JsonException e) {
				ArchiLogger.LogGenericException(e);

				if (Debugging.IsUserDebugging) {
					ArchiLogger.LogGenericDebug(string.Format(Strings.Content, response.Content));
				}

				return null;
			}

			return new ObjectResponse<T>(response, obj);
		}

		private async Task<BinaryResponse> UrlGetToBinaryWithProgress(string request, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			const byte printPercentage = 10;
			const byte maxBatches = 99 / printPercentage;

			using (HttpResponseMessage response = await UrlGetToHttp(request, referer, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false)) {
				if (response == null) {
					return null;
				}

				ArchiLogger.LogGenericDebug("0%...");

				uint contentLength = (uint) response.Content.Headers.ContentLength.GetValueOrDefault();

				using (MemoryStream ms = new MemoryStream((int) contentLength)) {
					try {
						using (Stream contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false)) {
							byte batch = 0;
							uint readThisBatch = 0;
							byte[] buffer = new byte[8192]; // This is HttpClient's buffer, using more doesn't make sense

							while (contentStream.CanRead) {
								int read = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
								if (read == 0) {
									break;
								}

								await ms.WriteAsync(buffer, 0, read).ConfigureAwait(false);

								if ((contentLength == 0) || (batch >= maxBatches)) {
									continue;
								}

								readThisBatch += (uint) read;

								if (readThisBatch < contentLength / printPercentage) {
									continue;
								}

								readThisBatch -= contentLength / printPercentage;
								ArchiLogger.LogGenericDebug(++batch * printPercentage + "%...");
							}
						}
					} catch (Exception e) {
						ArchiLogger.LogGenericDebuggingException(e);
						return null;
					}

					ArchiLogger.LogGenericDebug("100%");
					return new BinaryResponse(response, ms.ToArray());
				}
			}
		}

		private async Task<HttpResponseMessage> UrlGetToHttp(string request, string referer = null, HttpCompletionOption httpCompletionOptions = HttpCompletionOption.ResponseContentRead) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			HttpResponseMessage response = await UrlRequest(new Uri(request), HttpMethod.Get, null, referer, httpCompletionOptions).ConfigureAwait(false);
			return response;
		}

		private async Task<StringResponse> UrlGetToString(string request, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			using (HttpResponseMessage response = await UrlGetToHttp(request, referer).ConfigureAwait(false)) {
				return response != null ? new StringResponse(response, await response.Content.ReadAsStringAsync().ConfigureAwait(false)) : null;
			}
		}

		private async Task<StringResponse> UrlGetToStringRetry(string request, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			StringResponse response = null;

			for (byte i = 0; (i < MaxTries) && (response == null); i++) {
				response = await UrlGetToString(request, referer).ConfigureAwait(false);
			}

			if (response != null) {
				return response;
			}

			ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorRequestFailedTooManyTimes, MaxTries));
			ArchiLogger.LogGenericDebug(string.Format(Strings.ErrorFailingRequest, request));
			return null;
		}

		private async Task<BasicResponse> UrlHead(string request, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			using (HttpResponseMessage response = await UrlHeadToHttp(request, referer).ConfigureAwait(false)) {
				return response != null ? new BasicResponse(response) : null;
			}
		}

		private async Task<HttpResponseMessage> UrlHeadToHttp(string request, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			HttpResponseMessage response = await UrlRequest(new Uri(request), HttpMethod.Head, null, referer).ConfigureAwait(false);
			return response;
		}

		private async Task<HttpResponseMessage> UrlPostToHttp(string request, IReadOnlyCollection<KeyValuePair<string, string>> data = null, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			HttpResponseMessage response = await UrlRequest(new Uri(request), HttpMethod.Post, data, referer).ConfigureAwait(false);
			return response;
		}

		private async Task<StringResponse> UrlPostToString(string request, IReadOnlyCollection<KeyValuePair<string, string>> data = null, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			using (HttpResponseMessage response = await UrlPostToHttp(request, data, referer).ConfigureAwait(false)) {
				return response != null ? new StringResponse(response, await response.Content.ReadAsStringAsync().ConfigureAwait(false)) : null;
			}
		}

		private async Task<StringResponse> UrlPostToStringRetry(string request, IReadOnlyCollection<KeyValuePair<string, string>> data = null, string referer = null) {
			if (string.IsNullOrEmpty(request)) {
				ArchiLogger.LogNullError(nameof(request));
				return null;
			}

			StringResponse response = null;

			for (byte i = 0; (i < MaxTries) && (response == null); i++) {
				response = await UrlPostToString(request, data, referer).ConfigureAwait(false);
			}

			if (response != null) {
				return response;
			}

			ArchiLogger.LogGenericWarning(string.Format(Strings.ErrorRequestFailedTooManyTimes, MaxTries));
			ArchiLogger.LogGenericDebug(string.Format(Strings.ErrorFailingRequest, request));
			return null;
		}

		private async Task<HttpResponseMessage> UrlRequest(Uri requestUri, HttpMethod httpMethod, IReadOnlyCollection<KeyValuePair<string, string>> data = null, string referer = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead, byte maxRedirections = MaxTries) {
			if ((requestUri == null) || (httpMethod == null)) {
				ArchiLogger.LogNullError(nameof(requestUri) + " || " + nameof(httpMethod));
				return null;
			}

			HttpResponseMessage response;

			using (HttpRequestMessage request = new HttpRequestMessage(httpMethod, requestUri)) {
				if (data != null) {
					try {
						request.Content = new FormUrlEncodedContent(data);
					} catch (UriFormatException e) {
						ArchiLogger.LogGenericException(e);
						return null;
					}
				}

				if (!string.IsNullOrEmpty(referer)) {
					request.Headers.Referrer = new Uri(referer);
				}

				try {
					response = await HttpClient.SendAsync(request, httpCompletionOption).ConfigureAwait(false);
				} catch (Exception e) {
					ArchiLogger.LogGenericDebuggingException(e);
					return null;
				}
			}

			if (response == null) {
				return null;
			}

			if (response.IsSuccessStatusCode) {
				return response;
			}

			Uri redirectUri;

			using (response) {
				ushort status = (ushort) response.StatusCode;
				if ((status >= 300) && (status <= 399) && (maxRedirections > 0)) {
					redirectUri = response.Headers.Location;

					if (redirectUri.IsAbsoluteUri) {
						switch (redirectUri.Scheme) {
							case "http":
							case "https":
								break;
							default:
								// Invalid ones such as "steammobile"
								return null;
						}
					} else {
						redirectUri = new Uri(requestUri.GetLeftPart(UriPartial.Authority) + redirectUri);
					}
				} else {
					if (!Debugging.IsDebugBuild) {
						return null;
					}

					ArchiLogger.LogGenericDebug(string.Format(Strings.ErrorFailingRequest, requestUri));
					ArchiLogger.LogGenericDebug(string.Format(Strings.StatusCode, response.StatusCode));
					ArchiLogger.LogGenericDebug(string.Format(Strings.Content, await response.Content.ReadAsStringAsync().ConfigureAwait(false)));
					return null;
				}
			}

			return await UrlRequest(redirectUri, httpMethod, data, referer, httpCompletionOption, --maxRedirections).ConfigureAwait(false);
		}

		internal class BasicResponse {
			internal readonly Uri RequestUri;

			internal BasicResponse(HttpResponseMessage httpResponseMessage) {
				if (httpResponseMessage == null) {
					throw new ArgumentNullException(nameof(httpResponseMessage));
				}

				RequestUri = httpResponseMessage.RequestMessage.RequestUri;
			}

			internal BasicResponse(BasicResponse basicResponse) {
				if (basicResponse == null) {
					throw new ArgumentNullException(nameof(basicResponse));
				}

				RequestUri = basicResponse.RequestUri;
			}
		}

		internal sealed class BinaryResponse : BasicResponse {
			internal readonly byte[] Content;

			internal BinaryResponse(HttpResponseMessage httpResponseMessage, byte[] content) : base(httpResponseMessage) {
				if ((httpResponseMessage == null) || (content == null)) {
					throw new ArgumentNullException(nameof(httpResponseMessage) + " || " + nameof(content));
				}

				Content = content;
			}
		}

		internal sealed class HtmlDocumentResponse : BasicResponse {
			internal readonly HtmlDocument Content;

			internal HtmlDocumentResponse(StringResponse stringResponse) : base(stringResponse) {
				if (stringResponse == null) {
					throw new ArgumentNullException(nameof(stringResponse));
				}

				Content = StringToHtmlDocument(stringResponse.Content);
			}
		}

		internal sealed class ObjectResponse<T> : BasicResponse {
			internal readonly T Content;

			internal ObjectResponse(StringResponse stringResponse, T content) : base(stringResponse) {
				if (stringResponse == null) {
					throw new ArgumentNullException(nameof(stringResponse));
				}

				Content = content;
			}
		}

		internal sealed class StringResponse : BasicResponse {
			internal readonly string Content;

			internal StringResponse(HttpResponseMessage httpResponseMessage, string content) : base(httpResponseMessage) {
				if ((httpResponseMessage == null) || (content == null)) {
					throw new ArgumentNullException(nameof(httpResponseMessage) + " || " + nameof(content));
				}

				Content = content;
			}
		}

		internal sealed class XmlResponse : BasicResponse {
			internal readonly XmlDocument Content;

			internal XmlResponse(StringResponse stringResponse, XmlDocument content) : base(stringResponse) {
				if (stringResponse == null) {
					throw new ArgumentNullException(nameof(stringResponse));
				}

				Content = content;
			}
		}
	}
}
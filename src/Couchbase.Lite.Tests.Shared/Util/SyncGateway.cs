//
//  SyncGateway.cs
//
//  Author:
//  	Jim Borden  <jim.borden@couchbase.com>
//
//  Copyright (c) 2015 Couchbase, Inc All rights reserved.
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
//
using System;
using System.Net;
using NUnit.Framework;
using System.Text;
using Sharpen;
using System.IO;
using Couchbase.Lite.Util;
using System.Net.Http;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http.Headers;

#if NET_3_5
using WebRequest = System.Net.Couchbase.WebRequest;
using HttpWebResponse = System.Net.Couchbase.HttpWebResponse;
using WebException = System.Net.Couchbase.WebException;
#endif

namespace Couchbase.Lite.Tests
{
    public sealed class RemoteDatabase : IDisposable
    {
        private const string Tag = "RemoteDatabase";
        private readonly Uri _adminRemoteUri;
        private readonly Uri _remoteUri;
        private readonly HttpClient _httpClient;
        public readonly string Name;

        public Uri RemoteUri
        {
            get { return _remoteUri; }
        }

        internal RemoteDatabase(RemoteEndpoint parent, string name, string username = null, string password = null)
        {
            _adminRemoteUri = parent.AdminUri.AppendPath(name);
            _remoteUri = parent.RegularUri.AppendPath(name);

            var handler = new HttpClientHandler();
            if (!String.IsNullOrEmpty(username) && !String.IsNullOrEmpty(password)) {
                handler.Credentials = new NetworkCredential(username, password);
                handler.PreAuthenticate = true;
            }

            _httpClient = new HttpClient(handler, true);
            Name = name;
        }

        public void Create()
        {
            var putRequest = new HttpRequestMessage(HttpMethod.Put, _adminRemoteUri + "/");

            putRequest.Content = new StringContent(@"{""server"":""walrus:"",
                 ""users"": {
                    ""GUEST"": {""disabled"": false, ""admin_channels"": [""*""]},
                    ""jim"" : { ""password"": ""borden"", ""admin_channels"": [""*""]}
                  },
                 ""bucket"":""" + Name + @""",
                 ""sync"":""function(doc) {channel(doc.channels);}""}");

            putRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            try {
                var putResponse = _httpClient.SendAsync(putRequest).Result;
                if(putResponse.StatusCode != HttpStatusCode.PreconditionFailed) {
                    Assert.AreEqual(HttpStatusCode.Created, putResponse.StatusCode);
                } else {
                    Delete().ContinueWith(t => Create()).Wait();
                    return;
                }
            } catch(WebException ex) {
                if (ex.Status == WebExceptionStatus.ProtocolError) {
                    var response = ex.Response as HttpWebResponse;
                    if (response != null) {
                        Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode);
                        Delete().ContinueWith(t => Create()).Wait();
                        return;
                    } else {
                        Assert.Fail("Error from remote: {0}", response.StatusCode);
                    }
                } else {
                    Assert.Fail("Error from remote: {0}", ex);
                }
            }

            Thread.Sleep(500);
        }

        public Task Delete()
        {
            return Task.Delay(1000).ContinueWith(t =>
            {
                var server = _adminRemoteUri;
                var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, server);
                try {
                    var response = _httpClient.SendAsync(deleteRequest).Result;
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                } catch (WebException ex) {
                    if (ex.Status == WebExceptionStatus.ProtocolError) {
                        var response = ex.Response as HttpWebResponse;
                        if (response != null) {
                            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
                        } else {
                            Assert.Fail("Error from remote: {0}", response.StatusCode);
                        }
                    } else {
                        Assert.Fail("Error from remote: {0}", ex);
                    }
                }
            });
        }

        public void VerifyDocumentExists(string docId) 
        {
            var pathToDoc = _remoteUri.AppendPath(docId);
            Log.D(Tag, "Send http request to " + pathToDoc);

            var httpRequestDoneSignal = new CountdownEvent(1);
            Task.Factory.StartNew(() =>
            {
                HttpResponseMessage response;
                string responseString = null;
                try
                {
                    var responseTask = _httpClient.GetAsync(pathToDoc.ToString());
                    response = responseTask.Result;
                    var statusLine = response.StatusCode;
                    Assert.IsTrue(statusLine == HttpStatusCode.OK);
                    if (statusLine == HttpStatusCode.OK)
                    {
                        var responseStringTask = response.Content.ReadAsStringAsync();
                        responseStringTask.Wait(TimeSpan.FromSeconds(10));
                        responseString = responseStringTask.Result;
                        Assert.IsTrue(responseString.Contains(docId));
                        Log.D(Tag, "result: " + responseString);
                    }
                    else
                    {
                        var statusReason = response.ReasonPhrase;
                        response.Dispose();
                        throw new IOException(statusReason);
                    }
                }
                catch (ProtocolViolationException e)
                {
                    Assert.IsNull(e, "Got ClientProtocolException: " + e.Message);
                }
                catch (IOException e)
                {
                    Assert.IsNull(e, "Got IOException: " + e.Message);
                }

                httpRequestDoneSignal.Signal();
            });

            var result = httpRequestDoneSignal.Wait(TimeSpan.FromSeconds(30));
            Assert.IsTrue(result, "Could not retrieve the new doc from the sync gateway.");
        }

        public void DisableGuestAccess()
        {
            var url = _adminRemoteUri.AppendPath("_user/GUEST");
            var request = new HttpRequestMessage(HttpMethod.Put, url);
            var data = Manager.GetObjectMapper().WriteValueAsBytes(new Dictionary<string, object> { { "disabled", true } }).ToArray();
            request.Content = new ByteArrayContent(data);
            _httpClient.SendAsync(request).Wait();
        }

        public void RestoreGuestAccess()
        {
            var url = _adminRemoteUri.AppendPath("_user/GUEST");
            var request = new HttpRequestMessage(HttpMethod.Put, url);

            var data = Manager.GetObjectMapper().WriteValueAsBytes(new Dictionary<string, object> { 
                { "disabled", false },
                { "admin_channels", new List<object> { "*" }}
            }).ToArray();
            request.Content = new ByteArrayContent(data);
            _httpClient.SendAsync(request).Wait();
        }

        public HashSet<string> AddDocuments(int count, bool withAttachment)
        {
            var docList = new HashSet<string>();
            var json = CreateDocumentJson(withAttachment ? "attachment.png" : null).Substring(1);
            var beginning = Encoding.UTF8.GetBytes(@"{""docs"":[");

            var request = new HttpRequestMessage(HttpMethod.Post, _remoteUri.AppendPath("_bulk_docs"));
            var stream = new MemoryStream();
            request.Content = new StreamContent(stream);
            stream.Write(beginning, 0, beginning.Length);

            for (int i = 0; i < count; i++) {
                var docIdTimestamp = Convert.ToString(Runtime.CurrentTimeMillis());
                var docId = string.Format("doc{0}-{1}", i, docIdTimestamp);         

                docList.Add(docId);
                string nextJson = null;
                if(i != count-1) {
                    nextJson = String.Format(@"{{""_id"":""{0}"",{1},", docId, json);
                } else {
                    nextJson = String.Format(@"{{""_id"":""{0}"",{1}]}}", docId, json);
                }

                var jsonBytes = Encoding.UTF8.GetBytes(nextJson);
                stream.Write(jsonBytes, 0, jsonBytes.Length);
            }
                
            stream.Seek(0, SeekOrigin.Begin);
            var response = _httpClient.SendAsync(request).Result;
            stream.Dispose();
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

            return docList;
        }

        public void DeleteDocument(string docId, string revId)
        {
            var remoteUrl = new Uri(string.Format("{0}/{1}?rev={2}", _remoteUri, docId, revId));
            Log.D(Tag, "Send http request to " + remoteUrl);
            try
            {
                HttpResponseMessage response;
                //var request = new HttpRequestMessage();
                //request.Headers.Add("Accept", "*/*");
                //request.Method = HttpMethod.Delete;

                var deleteTask = _httpClient.DeleteAsync(remoteUrl);
                response = deleteTask.Result;
                var statusLine = response.StatusCode;
                Log.D(Tag, "Got response: " + statusLine);
                Assert.IsTrue(statusLine == HttpStatusCode.OK);
            }
            catch (ProtocolViolationException e)
            {
                Assert.IsNull(e, "Got ClientProtocolException: " + e.Message);
            }
            catch (IOException e)
            {
                Assert.IsNull(e, "Got IOException: " + e.Message);
            }

            WorkaroundSyncGatewayRaceCondition();
        }

        public void AddDocument(string docId, string attachmentName)
        {
            string docJson = CreateDocumentJson(attachmentName);

            // push a document to server
            var replicationUrlTrailingDoc1 = new Uri(string.Format("{0}/{1}", _remoteUri, docId));
            var pathToDoc1 = new Uri(replicationUrlTrailingDoc1, docId);
            Log.D(Tag, "Send http request to " + pathToDoc1);
            try
            {
                HttpResponseMessage response;
                //var request = new HttpRequestMessage();
                //request.Headers.Add("Accept", "*/*");

                var postTask = _httpClient.PutAsync(pathToDoc1.AbsoluteUri, new StringContent(docJson, Encoding.UTF8, "application/json"));
                response = postTask.Result;
                var statusLine = response.StatusCode;
                Log.D(Tag, "Got response: " + statusLine);
                Assert.IsTrue(statusLine == HttpStatusCode.Created);
            }
            catch (ProtocolViolationException e)
            {
                Assert.IsNull(e, "Got ClientProtocolException: " + e.Message);
            }
            catch (IOException e)
            {
                Assert.IsNull(e, "Got IOException: " + e.Message);
            }

            WorkaroundSyncGatewayRaceCondition();
        }

        private string CreateDocumentJson(string attachmentName)
        {
            if (attachmentName != null)
            {
                // add attachment to document
                var attachmentStream = (InputStream)GetAsset(attachmentName);
                var baos = new MemoryStream();
                attachmentStream.Wrapped.CopyTo(baos);
                attachmentStream.Dispose();
                var attachmentBase64 = Convert.ToBase64String(baos.ToArray());
                baos.Dispose();
                return String.Format("{{\"foo\":1,\"bar\":false, \"_attachments\": {{ \"i_use_couchdb.png\": {{ \"content_type\": \"image/png\", \"data\": \"{0}\" }} }} }}", attachmentBase64);
            }
            else
            {
                return @"{""foo"":1,""bar"":false,""type"":""jim""}";
            }
        }

        private Stream GetAsset(string name)
        {
            var assetPath = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".Assets." + name;
            Log.D(Tag, "Fetching assembly resource: " + assetPath);
            var stream = GetType().GetResourceAsStream(assetPath);
            return stream;
        }

        private void WorkaroundSyncGatewayRaceCondition() 
        {
            Thread.Sleep(2 * 100);
        }

        public void Dispose()
        {
            Delete().ContinueWith(t => _httpClient.Dispose());
        }
    }

    public sealed class SyncGateway : RemoteEndpoint
    {
        protected override string Type
        {
            get
            {
                return "SyncGateway";
            }
        }

        public SyncGateway(string protocol, string server) : base(protocol, server, 4984, 4985) {}
    }

    public sealed class CouchDB : RemoteEndpoint
    {
        protected override string Type
        {
            get
            {
                return "CouchDB";
            }
        }

        public CouchDB(string protocol, string server) : base(protocol, server, 5984, 5984) {}
    }
}


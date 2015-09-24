//
//  MultipartTest.cs
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
using NUnit.Framework;
using Couchbase.Lite.Support;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Sharpen;

namespace Couchbase.Lite
{
    public class MultipartTest : LiteTestCase
    {
        private const string ATTACH_TEST_DB_NAME = "attach_test";

        [Test]
        public void TestMultipartWriter()
        {
            const string expectedOutput = "\r\n--BOUNDARY\r\nContent-Length: 16\r\n\r\n<part the first>\r\n--BOUNDARY\r\nContent-Length: " +
                "10\r\nContent-Type: something\r\n\r\n<2nd part>\r\n--BOUNDARY--";
            for (var bufSize = 1; bufSize < expectedOutput.Length - 1; ++bufSize) {
                var mp = new MultipartWriter("foo/bar", "BOUNDARY");
                Assert.AreEqual("foo/bar; boundary=\"BOUNDARY\"", mp.ContentType);
                Assert.AreEqual("BOUNDARY", mp.Boundary);
                mp.AddData(Encoding.UTF8.GetBytes("<part the first>"));
                mp.SetNextPartHeaders(new Dictionary<string, string> {
                    { "Content-Type", "something" }
                });
                mp.AddData(Encoding.UTF8.GetBytes("<2nd part>"));
                Assert.AreEqual(expectedOutput.Length, mp.Length);

                var output = mp.AllOutput();
                Assert.AreEqual(expectedOutput, Encoding.UTF8.GetString(output.ToArray()));
                mp.Close();
            }
        }

        [Test]
        public void TestMultipartWriterGzipped()
        {
            var mp = new MultipartWriter("foo/bar", "BOUNDARY");
            var data1 = Enumerable.Repeat((byte)'*', 100);
            mp.SetNextPartHeaders(new Dictionary<string, string> { 
                { "Content-Type", "star-bellies" }
            });
            mp.AddGZippedData(data1);
            var output = mp.AllOutput();
            Assert.AreEqual(GetType().GetResourceAsStream("MultipartStars.mime").ReadAllBytes(), output);
        }
    }
}


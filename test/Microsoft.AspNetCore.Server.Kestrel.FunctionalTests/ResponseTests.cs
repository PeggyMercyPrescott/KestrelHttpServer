// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;

namespace Microsoft.AspNetCore.Server.Kestrel.FunctionalTests
{
    public class ResponseTests
    {
        [Fact]
        public async Task LargeDownload()
        {
            var hostBuilder = new WebHostBuilder()
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0/")
                .Configure(app =>
                {
                    app.Run(async context =>
                    {
                        var bytes = new byte[1024];
                        for (int i = 0; i < bytes.Length; i++)
                        {
                            bytes[i] = (byte)i;
                        }

                        context.Response.ContentLength = bytes.Length * 1024;

                        for (int i = 0; i < 1024; i++)
                        {
                            await context.Response.Body.WriteAsync(bytes, 0, bytes.Length);
                        }
                    });
                });

            using (var host = hostBuilder.Build())
            {
                host.Start();

                using (var client = new HttpClient())
                {
                    var response = await client.GetAsync($"http://localhost:{host.GetPort()}/");
                    response.EnsureSuccessStatusCode();
                    var responseBody = await response.Content.ReadAsStreamAsync();

                    // Read the full response body
                    var total = 0;
                    var bytes = new byte[1024];
                    var count = await responseBody.ReadAsync(bytes, 0, bytes.Length);
                    while (count > 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            Assert.Equal(total % 256, bytes[i]);
                            total++;
                        }
                        count = await responseBody.ReadAsync(bytes, 0, bytes.Length);
                    }
                }
            }
        }

        [Theory, MemberData(nameof(NullHeaderData))]
        public async Task IgnoreNullHeaderValues(string headerName, StringValues headerValue, string expectedValue)
        {
            var hostBuilder = new WebHostBuilder()
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0/")
                .Configure(app =>
                {
                    app.Run(async context =>
                    {
                        context.Response.Headers.Add(headerName, headerValue);

                        await context.Response.WriteAsync("");
                    });
                });

            using (var host = hostBuilder.Build())
            {
                host.Start();

                using (var client = new HttpClient())
                {
                    var response = await client.GetAsync($"http://localhost:{host.GetPort()}/");
                    response.EnsureSuccessStatusCode();

                    var headers = response.Headers;

                    if (expectedValue == null)
                    {
                        Assert.False(headers.Contains(headerName));
                    }
                    else
                    {
                        Assert.True(headers.Contains(headerName));
                        Assert.Equal(headers.GetValues(headerName).Single(), expectedValue);
                    }
                }
            }
        }

        [Fact]
        public async Task OnCompleteCalledEvenWhenOnStartingNotCalled()
        {
            var onStartingCalled = false;
            var onCompletedCalled = false;

            var hostBuilder = new WebHostBuilder()
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0/")
                .Configure(app =>
                {
                    app.Run(context =>
                    {
                        context.Response.OnStarting(() => Task.Run(() => onStartingCalled = true));
                        context.Response.OnCompleted(() => Task.Run(() => onCompletedCalled = true));

                        // Prevent OnStarting call (see Frame<T>.RequestProcessingAsync()).
                        throw new Exception();
                    });
                });

            using (var host = hostBuilder.Build())
            {
                host.Start();

                using (var client = new HttpClient())
                {
                    var response = await client.GetAsync($"http://localhost:{host.GetPort()}/");

                    Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                    Assert.False(onStartingCalled);
                    Assert.True(onCompletedCalled);
                }
            }
        }

        [Fact]
        public async Task OnStartingThrowsWhenSetAfterResponseHasAlreadyStarted()
        {
            InvalidOperationException ex = null;

            var hostBuilder = new WebHostBuilder()
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0/")
                .Configure(app =>
                {
                    app.Run(async context =>
                    {
                        await context.Response.WriteAsync("hello, world");
                        await context.Response.Body.FlushAsync();
                        ex = Assert.Throws<InvalidOperationException>(() => context.Response.OnStarting(_ => TaskCache.CompletedTask, null));
                    });
                });

            using (var host = hostBuilder.Build())
            {
                host.Start();

                using (var client = new HttpClient())
                {
                    var response = await client.GetAsync($"http://localhost:{host.GetPort()}/");

                    // Despite the error, the response had already started
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.NotNull(ex);
                }
            }
        }

        // https://github.com/aspnet/KestrelHttpServer/pull/1111/files#r80584475 explains the reason for this test.
        [Fact]
        public async Task SingleErrorResponseSentWhenAppSwallowsBadRequestException()
        {
            using (var server = new TestServer(async httpContext =>
            {
                try
                {
                    await httpContext.Request.Body.ReadAsync(new byte[1], 0, 1);
                }
                catch (BadHttpRequestException)
                {
                }
            }, new TestServiceContext()))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "POST / HTTP/1.1",
                        "Transfer-Encoding: chunked",
                        "",
                        "g",
                        "");
                    await connection.ReceiveForcedEnd(
                        "HTTP/1.1 400 Bad Request",
                        "Connection: close",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Content-Length: 0",
                        "",
                        "");
                }
            }
        }

        [Fact]
        public async Task TransferEncodingChunkedSetOnUnknownLengthHttp11Response()
        {
            using (var server = new TestServer(async httpContext =>
            {
                await httpContext.Response.WriteAsync("hello, ");
                await httpContext.Response.WriteAsync("world");
            }, new TestServiceContext()))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.Receive(
                        "HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Transfer-Encoding: chunked",
                        "",
                        "7",
                        "hello, ",
                        "5",
                        "world",
                        "0",
                        "",
                        "");
                }
            }
        }

        [Theory]
        [InlineData(204)]
        [InlineData(205)]
        [InlineData(304)]
        public async Task TransferEncodingChunkedNotSetOnNonBodyResponse(int statusCode)
        {
            using (var server = new TestServer(httpContext =>
            {
                httpContext.Response.StatusCode = statusCode;
                return TaskCache.CompletedTask;
            }, new TestServiceContext()))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.Receive(
                        $"HTTP/1.1 {Encoding.ASCII.GetString(ReasonPhrases.ToStatusBytes(statusCode))}",
                        $"Date: {server.Context.DateHeaderValue}",
                        "",
                        "");
                }
            }
        }

        [Fact]
        public async Task TransferEncodingNotSetOnHeadResponse()
        {
            using (var server = new TestServer(httpContext =>
            {
                return TaskCache.CompletedTask;
            }, new TestServiceContext()))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "HEAD / HTTP/1.1",
                        "",
                        "");
                    await connection.Receive(
                        $"HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "",
                        "");
                }
            }
        }

        [Fact]
        public async Task ResponseBodyNotWrittenOnHeadResponseAndLoggedOnlyOnce()
        {
            var mockKestrelTrace = new Mock<IKestrelTrace>();

            using (var server = new TestServer(async httpContext =>
            {
                await httpContext.Response.WriteAsync("hello, world");
                await httpContext.Response.Body.FlushAsync();
            }, new TestServiceContext { Log = mockKestrelTrace.Object }))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "HEAD / HTTP/1.1",
                        "",
                        "");
                    await connection.Receive(
                        $"HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "",
                        "");
                }
            }

            mockKestrelTrace.Verify(kestrelTrace =>
                kestrelTrace.ConnectionHeadResponseBodyWrite(It.IsAny<string>(), "hello, world".Length), Times.Once);
        }

        [Fact]
        public async Task WhenAppWritesMoreThanContentLengthWriteThrowsAndConnectionCloses()
        {
            var testLogger = new TestApplicationErrorLogger();
            var serviceContext = new TestServiceContext { Log = new TestKestrelTrace(testLogger) };

            using (var server = new TestServer(httpContext =>
            {
                httpContext.Response.ContentLength = 11;
                httpContext.Response.Body.Write(Encoding.ASCII.GetBytes("hello,"), 0, 6);
                httpContext.Response.Body.Write(Encoding.ASCII.GetBytes(" world"), 0, 6);
                return TaskCache.CompletedTask;
            }, serviceContext))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        $"HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Content-Length: 11",
                        "",
                        "hello,");
                }
            }

            var logMessage = Assert.Single(testLogger.Messages, message => message.LogLevel == LogLevel.Error);
            Assert.Equal(
                $"Response Content-Length mismatch: too many bytes written (12 of 11).",
                logMessage.Exception.Message);
        }

        [Fact]
        public async Task WhenAppWritesMoreThanContentLengthWriteAsyncThrowsAndConnectionCloses()
        {
            var testLogger = new TestApplicationErrorLogger();
            var serviceContext = new TestServiceContext { Log = new TestKestrelTrace(testLogger) };

            using (var server = new TestServer(async httpContext =>
            {
                httpContext.Response.ContentLength = 11;
                await httpContext.Response.WriteAsync("hello,");
                await httpContext.Response.WriteAsync(" world");
            }, serviceContext))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        $"HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Content-Length: 11",
                        "",
                        "hello,");
                }
            }

            var logMessage = Assert.Single(testLogger.Messages, message => message.LogLevel == LogLevel.Error);
            Assert.Equal(
                $"Response Content-Length mismatch: too many bytes written (12 of 11).",
                logMessage.Exception.Message);
        }

        [Fact]
        public async Task WhenAppWritesMoreThanContentLengthAndResponseNotStarted500ResponseSentAndConnectionCloses()
        {
            var testLogger = new TestApplicationErrorLogger();
            var serviceContext = new TestServiceContext { Log = new TestKestrelTrace(testLogger) };

            using (var server = new TestServer(async httpContext =>
            {
                httpContext.Response.ContentLength = 5;
                await httpContext.Response.WriteAsync("hello, world");
            }, serviceContext))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        $"HTTP/1.1 500 Internal Server Error",
                        "Connection: close",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Content-Length: 0",
                        "",
                        "");
                }
            }

            var logMessage = Assert.Single(testLogger.Messages, message => message.LogLevel == LogLevel.Error);
            Assert.Equal(
                $"Response Content-Length mismatch: too many bytes written (12 of 5).",
                logMessage.Exception.Message);
        }

        [Fact]
        public async Task WhenAppWritesLessThanContentLengthErrorLogged()
        {
            var testLogger = new TestApplicationErrorLogger();
            var serviceContext = new TestServiceContext { Log = new TestKestrelTrace(testLogger) };

            using (var server = new TestServer(async httpContext =>
            {
                httpContext.Response.ContentLength = 13;
                await httpContext.Response.WriteAsync("hello, world");
            }, serviceContext))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        $"HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Content-Length: 13",
                        "",
                        "hello, world");
                }
            }

            var errorMessage = Assert.Single(testLogger.Messages, message => message.LogLevel == LogLevel.Error);
            Assert.Equal(
                $"Response Content-Length mismatch: too few bytes written (12 of 13).",
                errorMessage.Exception.Message);
        }

        [Fact]
        public async Task WhenAppSetsContentLengthButDoesNotWriteBody500ResponseSentAndConnectionCloses()
        {
            var testLogger = new TestApplicationErrorLogger();
            var serviceContext = new TestServiceContext { Log = new TestKestrelTrace(testLogger) };

            using (var server = new TestServer(httpContext =>
            {
                httpContext.Response.ContentLength = 5;
                return TaskCache.CompletedTask;
            }, serviceContext))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        $"HTTP/1.1 500 Internal Server Error",
                        "Connection: close",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Content-Length: 0",
                        "",
                        "");
                }
            }

            var errorMessage = Assert.Single(testLogger.Messages, message => message.LogLevel == LogLevel.Error);
            Assert.Equal(
                $"Response Content-Length mismatch: too few bytes written (0 of 5).",
                errorMessage.Exception.Message);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WhenAppSetsContentLengthToZeroAndDoesNotWriteNoErrorIsThrown(bool flushResponse)
        {
            var testLogger = new TestApplicationErrorLogger();
            var serviceContext = new TestServiceContext { Log = new TestKestrelTrace(testLogger) };

            using (var server = new TestServer(async httpContext =>
            {
                httpContext.Response.ContentLength = 0;

                if (flushResponse)
                {
                    await httpContext.Response.Body.FlushAsync();
                }
            }, serviceContext))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.Receive(
                        $"HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Content-Length: 0",
                        "",
                        "");
                }
            }

            Assert.Equal(0, testLogger.ApplicationErrorsLogged);
        }

        // https://tools.ietf.org/html/rfc7230#section-3.3.3
        // If a message is received with both a Transfer-Encoding and a
        // Content-Length header field, the Transfer-Encoding overrides the
        // Content-Length.
        [Fact]
        public async Task WhenAppSetsTransferEncodingAndContentLengthWritingLessIsNotAnError()
        {
            var testLogger = new TestApplicationErrorLogger();
            var serviceContext = new TestServiceContext { Log = new TestKestrelTrace(testLogger) };

            using (var server = new TestServer(async httpContext =>
            {
                httpContext.Response.Headers["Transfer-Encoding"] = "chunked";
                httpContext.Response.ContentLength = 13;
                await httpContext.Response.WriteAsync("hello, world");
            }, serviceContext))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.Receive(
                        $"HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Transfer-Encoding: chunked",
                        "Content-Length: 13",
                        "",
                        "hello, world");
                }
            }

            Assert.Equal(0, testLogger.ApplicationErrorsLogged);
        }

        // https://tools.ietf.org/html/rfc7230#section-3.3.3
        // If a message is received with both a Transfer-Encoding and a
        // Content-Length header field, the Transfer-Encoding overrides the
        // Content-Length.
        [Fact]
        public async Task WhenAppSetsTransferEncodingAndContentLengthWritingMoreIsNotAnError()
        {
            var testLogger = new TestApplicationErrorLogger();
            var serviceContext = new TestServiceContext { Log = new TestKestrelTrace(testLogger) };

            using (var server = new TestServer(async httpContext =>
            {
                httpContext.Response.Headers["Transfer-Encoding"] = "chunked";
                httpContext.Response.ContentLength = 11;
                await httpContext.Response.WriteAsync("hello, world");
            }, serviceContext))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.Send(
                        "GET / HTTP/1.1",
                        "",
                        "");
                    await connection.Receive(
                        $"HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Transfer-Encoding: chunked",
                        "Content-Length: 11",
                        "",
                        "hello, world");
                }
            }

            Assert.Equal(0, testLogger.ApplicationErrorsLogged);
        }

        [Fact]
        public async Task HeadResponseCanContainContentLengthHeader()
        {
            var testLogger = new TestApplicationErrorLogger();
            var serviceContext = new TestServiceContext { Log = new TestKestrelTrace(testLogger) };

            using (var server = new TestServer(httpContext =>
            {
                httpContext.Response.ContentLength = 42;
                return TaskCache.CompletedTask;
            }, serviceContext))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.SendEnd(
                        "HEAD / HTTP/1.1",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        $"HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Content-Length: 42",
                        "",
                        "");
                }
            }
        }

        [Fact]
        public async Task HeadResponseCanContainContentLengthHeaderButBodyNotWritten()
        {
            var testLogger = new TestApplicationErrorLogger();
            var serviceContext = new TestServiceContext { Log = new TestKestrelTrace(testLogger) };

            using (var server = new TestServer(async httpContext =>
            {
                httpContext.Response.ContentLength = 12;
                await httpContext.Response.WriteAsync("hello, world");
            }, serviceContext))
            {
                using (var connection = server.CreateConnection())
                {
                    await connection.SendEnd(
                        "HEAD / HTTP/1.1",
                        "",
                        "");
                    await connection.ReceiveEnd(
                        $"HTTP/1.1 200 OK",
                        $"Date: {server.Context.DateHeaderValue}",
                        "Content-Length: 12",
                        "",
                        "");
                }
            }
        }

        public static TheoryData<string, StringValues, string> NullHeaderData
        {
            get
            {
                var dataset = new TheoryData<string, StringValues, string>();

                // Unknown headers
                dataset.Add("NullString", (string)null, null);
                dataset.Add("EmptyString", "", "");
                dataset.Add("NullStringArray", new string[] { null }, null);
                dataset.Add("EmptyStringArray", new string[] { "" }, "");
                dataset.Add("MixedStringArray", new string[] { null, "" }, "");
                // Known headers
                dataset.Add("Location", (string)null, null);
                dataset.Add("Location", "", "");
                dataset.Add("Location", new string[] { null }, null);
                dataset.Add("Location", new string[] { "" }, "");
                dataset.Add("Location", new string[] { null, "" }, "");

                return dataset;
            }
        }
    }
}

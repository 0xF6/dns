namespace DNS.Server
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;
    using System.IO;
    using Protocol;
    using Protocol.Utils;
    using Client;
    using Client.RequestResolver;
    using System.Runtime.InteropServices;

    public class DnsServer : IDisposable
    {
        private const int DEFAULT_PORT = 53;
        private const int UDP_TIMEOUT = 2000;

        public event EventHandler<RequestedEventArgs> Requested;
        public event EventHandler<RespondedEventArgs> Responded;
        public event EventHandler<EventArgs> Listening;
        public event EventHandler<ErroredEventArgs> Errored;

        private bool run = true;
        private bool disposed = false;
        private UdpClient udp;
        private readonly IRequestResolver resolver;

        public DnsServer(IRequestResolver masterFile, IPEndPoint endServer) :
            this(new FallbackRequestResolver(masterFile, new UdpRequestResolver(endServer)))
        { }

        public DnsServer(IRequestResolver masterFile, IPAddress endServer, int port = DEFAULT_PORT) :
            this(masterFile, new IPEndPoint(endServer, port))
        { }

        public DnsServer(IRequestResolver masterFile, string endServer, int port = DEFAULT_PORT) :
            this(masterFile, IPAddress.Parse(endServer), port)
        { }

        public DnsServer(IPEndPoint endServer) :
            this(new UdpRequestResolver(endServer))
        { }

        public DnsServer(IPAddress endServer, int port = DEFAULT_PORT) :
            this(new IPEndPoint(endServer, port))
        { }

        public DnsServer(string endServer, int port = DEFAULT_PORT) :
            this(IPAddress.Parse(endServer), port)
        { }

        public DnsServer(IRequestResolver resolver)
        {
            this.resolver = resolver;
        }

        public Task Listen(int port = DEFAULT_PORT, IPAddress ip = null)
        {
            return Listen(new IPEndPoint(ip ?? IPAddress.Any, port));
        }

        public async Task Listen(IPEndPoint endpoint)
        {
            await Task.Yield();

            var tcs = new TaskCompletionSource<object>();

            if (run)
            {
                try
                {
                    udp = new UdpClient(endpoint);

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        // Configure UdpClient to ignore PORT_UNREACHABLE ICMP messages.
                        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
                        var inValue = new byte[] { 0, 0, 0, 0 };
                        var outValue = new byte[] { 0, 0, 0, 0 };

                        udp.Client.IOControl(SIO_UDP_CONNRESET, inValue, outValue);
                    }


                }
                catch (SocketException e)
                {
                    OnError(e);
                    return;
                }
            }

            void ReceiveCallback(IAsyncResult result)
            {
                try
                {
                    var remote = new IPEndPoint(0, 0);
                    var data = udp.EndReceive(result, ref remote);
                    HandleRequest(data, remote);
                }
                catch (ObjectDisposedException)
                {
                    // run should already be false
                    run = false;
                }
                catch (SocketException e)
                {
                    OnError(e);
                }

                if (run)
                {
                    try
                    {
                        udp.BeginReceive(ReceiveCallback, null);
                    }
                    catch (Exception e)
                    {
                        OnError(e);
                        tcs.SetResult(null);
                    }
                }
                else
                    tcs.SetResult(null);
            }

            udp.BeginReceive(ReceiveCallback, null);
            OnEvent(Listening, EventArgs.Empty);
            await tcs.Task;
        }

        public void Dispose() => Dispose(true);

        protected virtual void OnEvent<T>(EventHandler<T> handler, T args) => handler?.Invoke(this, args);

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;

                if (disposing)
                {
                    run = false;
                    udp?.Dispose();
                }
            }
        }

        private void OnError(Exception e) => OnEvent(Errored, new ErroredEventArgs(e));

        private async void HandleRequest(byte[] data, IPEndPoint remote)
        {
            Request request = null;

            try
            {
                request = Request.FromArray(data);
                OnEvent(Requested, new RequestedEventArgs(request, data, remote));

                var response = await resolver.Resolve(request);

                OnEvent(Responded, new RespondedEventArgs(request, response, data, remote));
                await udp
                    .SendAsync(response.ToArray(), response.Size, remote)
                    .WithCancellationTimeout(UDP_TIMEOUT);
            }
            catch (SocketException e) { OnError(e); }
            catch (ArgumentException e) { OnError(e); }
            catch (IndexOutOfRangeException e) { OnError(e); }
            catch (OperationCanceledException e) { OnError(e); }
            catch (IOException e) { OnError(e); }
            catch (ObjectDisposedException e) { OnError(e); }
            catch (ResponseException e)
            {
                var response = e.Response ?? Response.FromRequest(request);

                try
                {
                    await udp
                        .SendAsync(response.ToArray(), response.Size, remote)
                        .WithCancellationTimeout(UDP_TIMEOUT);
                }
                catch (SocketException) { }
                catch (OperationCanceledException) { }
                finally { OnError(e); }
            }
        }

        public class RequestedEventArgs : EventArgs
        {
            public RequestedEventArgs(IRequest request, byte[] data, IPEndPoint remote)
            {
                Request = request;
                Data = data;
                Remote = remote;
            }

            public IRequest Request { get; }
            public byte[] Data { get; }
            public IPEndPoint Remote { get; }
        }

        public class RespondedEventArgs : EventArgs
        {
            public RespondedEventArgs(IRequest request, IResponse response, byte[] data, IPEndPoint remote)
            {
                Request = request;
                Response = response;
                Data = data;
                Remote = remote;
            }

            public IRequest Request
            {
                get;
                private set;
            }

            public IResponse Response
            {
                get;
                private set;
            }

            public byte[] Data
            {
                get;
                private set;
            }

            public IPEndPoint Remote
            {
                get;
                private set;
            }
        }

        public class ErroredEventArgs : EventArgs
        {
            public ErroredEventArgs(Exception e)
            {
                Exception = e;
            }

            public Exception Exception
            {
                get;
                private set;
            }
        }

        private class FallbackRequestResolver : IRequestResolver
        {
            private readonly IRequestResolver[] resolvers;

            public FallbackRequestResolver(params IRequestResolver[] resolvers) => this.resolvers = resolvers;

            public async Task<IResponse> Resolve(IRequest request)
            {
                IResponse response = null;

                foreach (var resolver in resolvers)
                {
                    response = await resolver.Resolve(request);
                    if (response.AnswerRecords.Count > 0)
                        break;
                }

                return response;
            }
        }
    }
}

using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class TcpSpiceAPI : ISpiceAPI
{
    private TcpClient _client;
    private readonly string _host;
    private readonly ushort _port;

    private readonly ConcurrentQueue<Action> _sendTasks = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stopThread = new();
    private readonly ManualResetEvent _queueEvent = new(false);

    private bool _disposed = false;
    private volatile bool _trying;

    public bool Connected => _client != null && _client.Connected;
    public string SpiceHost { get; private set; }

    public TcpSpiceAPI(string host, ushort port)
    {
        _host = host;
        _port = port;
        _trying = false;

        SpiceHost = $"{host}:{port}";

        _client = null;
        _ = TryConnectAsync();

        _thread = new Thread(SendThread);
        _thread.Start();
    }

    // Don't care if connection is good
    // We only need to send data when connected
    async Task TryConnectAsync()
    {
        if (_trying || _disposed)
            return;

        _trying = true;

        if (_client is not null && _client.Connected)
        {
            try
            {
                await _client.Client.SendAsync(Array.Empty<byte>());

                // if connection is good don't do anything
                _trying = false;
                return;
            }
            catch(SocketException)
            {
                _client = null;
            }
        }

        try
        {
            _client = new TcpClient();
            _client.NoDelay = true;
            _client.SendTimeout = 100;
            
            await _client.ConnectAsync(_host, _port);
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Failed to connect to spice api ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            _trying = false;
        }
    }

    // Called by a timer
    // To reconnect if disconnected
    public void GuardConnection()
    {
        // send zero sized packet to check if it is actually connected
        if (_client != null && _client.Connected)
        {
            try
            {
                _ = _client.Client.SendAsync(Array.Empty<byte>());
            }
            catch
            {
                // shhhh, we handle it later
            }
        }
            

        _ = TryConnectAsync();
    }

    void SendThread()
    {
        while (!_stopThread.IsCancellationRequested)
        {
            _queueEvent.WaitOne();

            while (true)
            {
                if (!_sendTasks.TryDequeue(out var task))
                    break;

                // Don't wait for connect, just discard
                task();
            }

            _queueEvent.Reset();
        }

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Send(string data)
    {
        void SendAction()
        {
            if (!_client.Connected)
                return;

            Span<byte> bytes = stackalloc byte[data.Length + 1];
            Encoding.ASCII.GetBytes(data, bytes);
            bytes[^1] = 0;

            try
            {
                _client.Client.Send(bytes);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"failed to send to spice ({ex.GetType().Name}): {ex.Message}");
            }

        }

        _sendTasks.Enqueue(SendAction);
        _queueEvent.Set();
    }

    int _lastId = 0;

    bool[] _lastButtonState = new bool[12];
    string[] packetParamsBuffer = new string[12];

    public void SendButtonsState(ReadOnlySpan<int> state, bool delta = true)
    {
        int paramCount = 0;
        for (int i = 0; i < 12; i++)
        {
            var newState = state[i] > 0;
            if (delta && newState == _lastButtonState[i])
                continue;

            _lastButtonState[i] = newState;
            packetParamsBuffer[paramCount++] = $"[\"Button {i + 1}\",{(newState ? "1" : "0")}]";
        }

        if (paramCount == 0)
            return;

        var paramStr = string.Join(',', packetParamsBuffer.Take(paramCount));
        Send($"{{\"id\":{_lastId++},\"module\":\"buttons\",\"function\":\"write\",\"params\":[{paramStr}]}}");
    }

    float _lastLeftFader = -1;
    float _lastRightFader = -1;

    public void SendAnalogsState(float left, float right, bool delta = true)
    {
        int paramCount = 0;
        if (!delta || left != _lastLeftFader)
        {
            _lastLeftFader = left;
            packetParamsBuffer[paramCount++] = $"[\"Fader-L\",{left:F2}]";
        }

        if (!delta || right != _lastRightFader)
        {
            _lastRightFader = right;
            packetParamsBuffer[paramCount++] = $"[\"Fader-R\",{right:F2}]";
        }

        if (paramCount == 0)
            return;

        var paramStr = string.Join(',', packetParamsBuffer.Take(paramCount));
        Send($"{{\"id\":{_lastId++},\"module\":\"analogs\",\"function\":\"write\",\"params\":[{paramStr}]}}");
    }

    public void Dispose()
    {
        if (_disposed) return;

        _stopThread.Cancel();
        _queueEvent.Set();
        _thread.Join();

        _queueEvent.Dispose();
        _stopThread.Dispose();

        if (_client != null)
        {
            if(_client.Connected)
                _client.Close();

            _client.Dispose();
        }
    }
}

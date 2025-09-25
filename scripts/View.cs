using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

record Finger
{
    public Finger(Vector2 pos, int idx)
    {
        Position = pos;
        Index = idx;
    }

    public Vector2 Position { get; set; }
    public int Index { get; init; }
}
public partial class View : Node
{
    const int FADER_DEAD_ZONE = 10;

    private readonly object _fingerLock = new object();
    private readonly Dictionary<int, Finger> _fingers = [];
    private List<ColorRect> _buttons;
    private int[] _laneState;
    private Config _config;
    private Window _window;

    private Finger _leftFaderFinger;
    private Finger _rightFaderFinger;
    private float _leftFaderAnalog = 0.5f;
    private float _rightFaderAnalog = 0.5f;
    private int _leftFaderDir = 0;
    private int _rightFaderDir = 0;
    private TextureRect _leftFader;
    private TextureRect _rightFader;
    private Label _faderPositionLabel;
    private Label _connectionStateLabel;
    private SpiceAPI _connection;
    private static readonly Color COLOR_TOUCH = new Color(1.0f, 1.0f, 1.0f, 0.3f);
    private static readonly Color COLOR_NORMAL = Color.FromHtml("#000");

    public override void _Ready()
    {
        _config = Config.EnsureInited();

        _window = GetWindow();

        var buttons = GetNode("UI/Lane/Buttons");
        _buttons = [.. buttons.GetChildren()
            .Where(x => x is ColorRect)
            .Select(x => x as ColorRect)];

        _laneState = new int[_buttons.Count];

        _leftFader = GetNode<TextureRect>("UI/Fader/LeftFader");
        _rightFader = GetNode<TextureRect>("UI/Fader/RightFader");

        _faderPositionLabel = GetNode<Label>("UI/Status/HBoxContainer/FaderPosition");
        _connectionStateLabel = GetNode<Label>("UI/Status/HBoxContainer/ConnectionStatus");

        var spiceHostLabel = GetNode<Label>("UI/Status/HBoxContainer/SpiceApiAddress");

        _connection = new SpiceAPI(_config.SpiceApiHost, _config.SpiceApiPort);
        spiceHostLabel.Text = _connection.SpiceHost;

        var faderArea = GetNode<Control>("UI/Fader");
        var laneArea = GetNode<Control>("UI/Lane");

        faderArea.AnchorBottom = _config.FaderAreaSize;
        laneArea.AnchorTop = 1.0f - _config.FaderAreaSize;

        _guard = GetNode<Timer>("ConnectionGuard");
        _guard.Timeout += Guard_Timeout;
        _guard.Start();
    }

    private void Guard_Timeout()
    {
        _connection.GuardConnection();
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventScreenTouch touch)
        {
            if (touch.Pressed)
            {
                lock (_fingerLock)
                {
                    _fingers[touch.Index] = new Finger(touch.Position, touch.Index);
                }
            }
            else
            {
                lock (_fingerLock)
                {
                    _fingers.Remove(touch.Index);
                }
            }

            _window.SetInputAsHandled();
        }
        else if (e is InputEventScreenDrag drag)
        {
            lock (_fingerLock)
            {
                _fingers[drag.Index].Position = drag.Position;
            }

            _window.SetInputAsHandled();
        }
    }

    private static (float, bool) UpdateFaderAnalog(int dir, float analog, float frameTime)
    {
        if (dir == 0)
        {
            if (analog == 0.5f)
                return (analog, false);

            analog += (0.5f - analog) * 1.92f;

            if (Mathf.Abs(0.5f - analog) < 0.001f)
            {
                analog = 0.5f;
            }
        }
        else
        {
            var dest = dir > 0 ? 1 : 0;
            if (analog == dest)
            {
                return (analog, false);
            }

            analog += (dest - analog) / 6;

            if (Mathf.Abs(dest - analog) < 0.001f)
            {
                analog = dest;
            }
        }

        return (analog, true);
    }

    private void UpdateFaderState(List<Finger> fingers, float frameTime)
    {
        var halfHeight = _window.Size.Y * _config.FaderAreaSize;
        var halfWidth = _window.Size.X / 2;

        if (_leftFaderFinger is null)
        {
            _leftFaderFinger = fingers.FirstOrDefault(v =>
            {
                if (v.Position.Y > halfHeight)
                    return false;

                if (_rightFaderFinger is null)
                {
                    if (v.Position.X > halfWidth)
                        return false;

                    return true;
                }

                if (v.Index == _rightFaderFinger.Index)
                    return false;

                if (v.Position.X > _rightFaderFinger.Position.X)
                    return false;

                return true;
            });
        }
        else
        {
            var newState = fingers.FirstOrDefault(v => v.Index == _leftFaderFinger.Index);

            if (newState != null)
            {
                var delta = newState.Position - _leftFaderFinger.Position;
                if (delta.Length() > FADER_DEAD_ZONE)
                    _leftFaderDir = Math.Sign(delta.X);
            }
            else
            {
                _leftFaderDir = 0;
            }

            _leftFaderFinger = newState;
        }

        if (_rightFaderFinger is null)
        {
            _rightFaderFinger = fingers.FirstOrDefault(v =>
            {
                if (v.Position.Y > halfHeight)
                    return false;

                if (_leftFaderFinger is null)
                {
                    if (v.Position.X < halfWidth)
                        return false;

                    return true;
                }

                if (v.Index == _leftFaderFinger.Index)
                    return false;

                if (v.Position.X < _leftFaderFinger.Position.X)
                    return false;

                return true;
            });
        }
        else
        {
            var newState = fingers.FirstOrDefault(v => v.Index == _rightFaderFinger.Index);
            if (newState != null)
            {
                var delta = newState.Position - _rightFaderFinger.Position;
                if (delta.Length() > FADER_DEAD_ZONE)
                    _rightFaderDir = Math.Sign(delta.X);
            }
            else
            {
                _rightFaderDir = 0;
            }

            _rightFaderFinger = newState;
        }

        var leftUpdated = false;
        (_leftFaderAnalog, leftUpdated) = UpdateFaderAnalog(_leftFaderDir, _leftFaderAnalog, frameTime);

        var rightUpdated = false;
        (_rightFaderAnalog, rightUpdated) = UpdateFaderAnalog(_rightFaderDir, _rightFaderAnalog, frameTime);

        if (leftUpdated || rightUpdated)
        {
            _connection.SendAnalogsState(_leftFaderAnalog, _rightFaderAnalog);
        }
    }

    private void UpdateLaneState(List<Finger> fingers)
    {
        Array.Fill(_laneState, 0);

        if (fingers.Count != 0)
        {
            var halfHeight = _window.Size.Y * _config.FaderAreaSize;
            var laneWidth = _window.Size.X / (float)_laneState.Length;

            foreach (var finger in fingers)
            {
                var pos = finger.Position;

                if (pos.Y < halfHeight)
                    continue;

                var lane = (int)(pos.X / laneWidth);
                _laneState[lane]++;
            }
        }

        _connection.SendButtonsState(_laneState);
    }

    DateTime? optionStartHoldTime = null;
    private Timer _guard;

    private void DetectOptionHold(List<Finger> fingers)
    {
        var isOptionHold = fingers.Any(f => f.Position.X < 128 && f.Position.Y < 128);
        if (isOptionHold)
        {
            if (optionStartHoldTime is null)
            {
                optionStartHoldTime = DateTime.Now;
                return;
            }

            var duration = DateTime.Now - optionStartHoldTime.Value;

            if (duration.TotalSeconds > 1)
            {
                GetTree().ChangeSceneToFile("res://Options.tscn");
            }

            return;
        }

        optionStartHoldTime = null;
    }

    public override void _PhysicsProcess(double frameTime)
    {
        List<Finger> fingers;
        lock (_fingerLock)
        {
            fingers = [.. _fingers.Values.Select(x => x with { })];
        }

        UpdateFaderState(fingers, (float)frameTime);
        UpdateLaneState(fingers);
        DetectOptionHold(fingers);
    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            var state = _laneState[i];
            var button = _buttons[i];

            if (state > 0)
                button.Color = COLOR_TOUCH;
            else
                button.Color = COLOR_NORMAL;
        }

        _leftFader.AnchorLeft = _leftFader.AnchorRight = _leftFaderAnalog * 0.5f;
        _rightFader.AnchorLeft = _rightFader.AnchorRight = 0.5f + _rightFaderAnalog * 0.5f;

        _faderPositionLabel.Text = $"{_leftFaderAnalog:F2}, {_rightFaderAnalog:F2}";
        _connectionStateLabel.Text = _connection.Connected ? "CONNECTED" : "DISCONNECTED";
    }

    public override void _ExitTree()
    {
        _guard.Stop();
        _connection.Dispose();
        _connection = null;
    }
}

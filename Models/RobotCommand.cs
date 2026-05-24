namespace MyRoboticsInspector.Models;

public enum RobotCommandType
{
    Stop,
    MoveForward,
    MoveBackward,
    TurnLeft,
    TurnRight,
    LightOn,
    LightOff,
    LightBrightness,
    CameraPan,
    CameraTilt,
    CameraZoom,
    Custom
}

/// <summary>
/// Generic robot command. The actual wire format is decided by the IRobotProtocol implementation.
/// Value is interpreted per command (e.g. speed 0..1 for movement, angle for camera, 0..100 for light).
/// </summary>
public record RobotCommand(RobotCommandType Type, float? Value = null, string? Payload = null);

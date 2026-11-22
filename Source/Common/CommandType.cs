namespace Multiplayer.Common;

public enum CommandType : byte
{
    // Global scope
    GlobalTimeSpeed,
    TimeSpeedVote,
    PauseAll,
    CreateJoinPoint,
    InitPlayerData,

    // Mixed scope
    Sync,
    DebugTools,

    // Map scope
    MapTimeSpeed,
    Designator,
    PlayerCount,

    // Global scope. Append new values so recorded commands keep their IDs.
    GravshipCutsceneEnded,
}

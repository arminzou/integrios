namespace Integrios.Application.Connections;

public sealed class ConnectionAuthoringConflictException()
    : Exception("Connection authoring is busy. Retry the request.");

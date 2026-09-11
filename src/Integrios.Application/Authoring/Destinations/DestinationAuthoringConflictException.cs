namespace Integrios.Application.Authoring.Destinations;

public sealed class DestinationAuthoringConflictException()
    : Exception("Destination authoring is busy. Retry the request.");

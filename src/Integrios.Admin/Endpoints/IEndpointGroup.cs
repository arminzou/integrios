namespace Integrios.Admin.Endpoints;

public interface IEndpointGroup
{
    string Prefix { get; }
    void Map(RouteGroupBuilder group);
}

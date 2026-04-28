namespace Integrios.Ingress.Endpoints;

public interface IEndpointGroup
{
    string Prefix { get; }
    void Map(RouteGroupBuilder group);
}

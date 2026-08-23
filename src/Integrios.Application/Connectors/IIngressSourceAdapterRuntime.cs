namespace Integrios.Application.Connectors;

public interface IIngressSourceAdapterRuntime
{
    IIngressSourceAdapter GetRequired(string key, int contractVersion);
}

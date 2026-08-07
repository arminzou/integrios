namespace Integrios.Application.Integrations;

public interface IIngressSourceAdapterRuntime
{
    IIngressSourceAdapter GetRequired(string key, int contractVersion);
}

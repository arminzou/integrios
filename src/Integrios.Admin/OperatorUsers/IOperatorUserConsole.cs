namespace Integrios.Admin.OperatorUsers;

internal interface IOperatorUserConsole
{
    bool IsInteractive { get; }
    ConsoleKeyInfo ReadKey();
    void Write(string value);
    void WriteLine(string value = "");
    void WriteError(string value);
}

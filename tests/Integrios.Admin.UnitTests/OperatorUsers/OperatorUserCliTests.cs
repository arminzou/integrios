using Integrios.Admin.OperatorUsers;

namespace Integrios.Admin.UnitTests.OperatorUsers;

public sealed class OperatorUserCliTests
{
    [Fact]
    public void PasswordPrompt_FailsClosedWithoutInteractiveTerminal()
    {
        var console = new FakeConsole(isInteractive: false, []);

        OperatorUserCli.TryReadConfirmedPassword(console, out string password).ShouldBeFalse();

        password.ShouldBeEmpty();
        console.Error.ShouldContain("interactive terminal", Case.Insensitive);
        console.ReadCount.ShouldBe(0);
    }

    [Fact]
    public void PasswordPrompt_MasksConfirmsAndHandlesBackspace()
    {
        ConsoleKeyInfo[] keys =
        [
            .. Keys("fifteen-letters?"), Backspace(), Key('!'), Enter(),
            .. Keys("fifteen-letters!"), Enter(),
        ];
        var console = new FakeConsole(isInteractive: true, keys);

        OperatorUserCli.TryReadConfirmedPassword(console, out string password).ShouldBeTrue();

        password.ShouldBe("fifteen-letters!");
        console.Output.ShouldNotContain(password, Case.Sensitive);
        console.Output.ShouldNotContain("?", Case.Sensitive);
        console.Output.ShouldContain("*");
        console.Error.ShouldBeEmpty();
    }

    [Fact]
    public void PasswordPrompt_RejectsConfirmationMismatchWithoutEchoingEitherValue()
    {
        ConsoleKeyInfo[] keys =
        [
            .. Keys("fifteen-letters!"), Enter(),
            .. Keys("different-value!"), Enter(),
        ];
        var console = new FakeConsole(isInteractive: true, keys);

        OperatorUserCli.TryReadConfirmedPassword(console, out string password).ShouldBeFalse();

        password.ShouldBeEmpty();
        console.Output.ShouldNotContain("fifteen-letters!", Case.Sensitive);
        console.Output.ShouldNotContain("different-value!", Case.Sensitive);
        console.Error.ShouldContain("does not match", Case.Insensitive);
    }

    [Fact]
    public void TryReadOptions_AcceptsKnownOptionsInEitherOrder()
    {
        string[] args = ["operator-user", "create", "--email", "operator@example.com", "--display-name", "Operator"];

        bool parsed = OperatorUserCli.TryReadOptions(
            args,
            ["--display-name", "--email"],
            out Dictionary<string, string> options);

        parsed.ShouldBeTrue();
        options["--email"].ShouldBe("operator@example.com");
        options["--display-name"].ShouldBe("Operator");
    }

    [Theory]
    [InlineData("--password", "secret")]
    [InlineData("--email", "one@example.com", "--email", "two@example.com")]
    [InlineData("--email")]
    public void TryReadOptions_RejectsUnknownDuplicateOrValuelessOptions(params string[] tail)
    {
        string[] args = ["operator-user", "create", .. tail];

        OperatorUserCli.TryReadOptions(args, ["--email"], out _).ShouldBeFalse();
    }

    private static IEnumerable<ConsoleKeyInfo> Keys(string value) => value.Select(Key);

    private static ConsoleKeyInfo Key(char value) =>
        new(value, ConsoleKey.A, shift: false, alt: false, control: false);

    private static ConsoleKeyInfo Enter() =>
        new('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);

    private static ConsoleKeyInfo Backspace() =>
        new('\b', ConsoleKey.Backspace, shift: false, alt: false, control: false);

    private sealed class FakeConsole(bool isInteractive, IEnumerable<ConsoleKeyInfo> keys)
        : IOperatorUserConsole
    {
        private readonly Queue<ConsoleKeyInfo> keys = new(keys);
        private readonly StringWriter output = new();
        private readonly StringWriter error = new();

        public bool IsInteractive { get; } = isInteractive;
        public string Output => output.ToString();
        public string Error => error.ToString();
        public int ReadCount { get; private set; }

        public ConsoleKeyInfo ReadKey()
        {
            ReadCount++;
            return keys.Dequeue();
        }

        public void Write(string value) => output.Write(value);
        public void WriteLine(string value = "") => output.WriteLine(value);
        public void WriteError(string value) => error.WriteLine(value);
    }
}

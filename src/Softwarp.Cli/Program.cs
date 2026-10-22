using System.Runtime.InteropServices;
using Softwarp.Bindings;

using var cancel = new CancellationTokenSource();
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
return await Softwarp.Cli.Cli.RunAsync(args, () => Composition.Create(Composition.DefaultPath), Console.Out, Console.Error,
    cancel.Token);

void Stop(PosixSignalContext signal)
{
    signal.Cancel = true;
    cancel.Cancel();
}

using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using WebResponse = EliteStudio.App.WebResponse;     // System.Net has an older type with the same name

namespace EliteStudio.Tests;

// The server inside the Windows window's message loop, which only the Windows build has.
public sealed partial class WebServerTests
{
    [Fact]
    public async Task Handlers_run_on_the_thread_that_started_the_server_when_it_has_a_message_loop()
    {
        // The app starts the server on its UI thread; game state lives there, so handlers must run there too.
        int handlerThread = 0, uiThread = 0;
        var started = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Thread(() =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            web.Map("GET", "/api/where", async _ =>
            {
                await Task.Delay(1);   // still the same thread after waiting
                handlerThread = Environment.CurrentManagedThreadId;
                return WebResponse.Json(new { ok = true });
            });
            started.SetResult(web.Start(47850) ? Dispatcher.CurrentDispatcher : throw new InvalidOperationException("no free port"));
            Dispatcher.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Dispatcher dispatcher = await started.Task;
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/where", web.Token)).Status);
            Assert.NotEqual(0, uiThread);
            Assert.Equal(uiThread, handlerThread);
        }
        finally
        {
            dispatcher.InvokeShutdown();
            ui.Join();
        }
    }
}

using Servidor.Negocio.Orquestacion;

namespace Servidor.Negocio.Tests.Orquestacion;

/// <summary>Garantías de la consulta compartida: cancelación por referencias, espera con token propio, y que nadie herede una operación ya cancelada.</summary>
public class DeduplicadorTests
{
    private static async Task Esperar(Func<bool> condicion)
    {
        for (var i = 0; i < 500 && !condicion(); i++) await Task.Delay(5);
        Assert.True(condicion(), "La condición no se cumplió a tiempo.");
    }

    [Fact]
    public async Task Quien_llega_cuando_la_ultima_espera_ya_cancelo_la_operacion_empieza_una_nueva_y_no_hereda_la_cancelacion()
    {
        var deduplicador = new Deduplicador<string, string>();
        var liberarPrimera = new TaskCompletionSource();
        var invocaciones = 0;
        var primeraObservoCancelacion = new TaskCompletionSource();

        async Task<string> Fabrica(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref invocaciones);
            if (n > 1) return "segunda";
            ct.Register(() => primeraObservoCancelacion.TrySetResult());
            await liberarPrimera.Task;      // la primera operación IGNORA la cancelación hasta que se libere: sigue «en vuelo» tras cancelarse
            ct.ThrowIfCancellationRequested();
            return "primera";
        }

        using var cts = new CancellationTokenSource();
        var a = deduplicador.EjecutarAsync("k", Fabrica, cts.Token);
        await Esperar(() => invocaciones == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        await primeraObservoCancelacion.Task.WaitAsync(TimeSpan.FromSeconds(5));   // la operación quedó cancelada pero todavía no terminó

        var b = await deduplicador.EjecutarAsync("k", Fabrica, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        liberarPrimera.SetResult();

        Assert.Equal("segunda", b);
        Assert.Equal(2, invocaciones);
    }

    [Fact]
    public async Task Al_terminar_la_operacion_cancelada_no_retira_a_la_nueva_operacion_de_la_misma_clave()
    {
        var deduplicador = new Deduplicador<string, string>();
        var liberarPrimera = new TaskCompletionSource();
        var liberarSegunda = new TaskCompletionSource();
        var invocaciones = 0;

        async Task<string> Fabrica(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref invocaciones);
            if (n == 1) { await liberarPrimera.Task; return "primera"; }
            await liberarSegunda.Task;
            return "segunda";
        }

        using var cts = new CancellationTokenSource();
        var a = deduplicador.EjecutarAsync("k", Fabrica, cts.Token);
        await Esperar(() => invocaciones == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);

        var b = deduplicador.EjecutarAsync("k", Fabrica, CancellationToken.None);   // nueva operación
        await Esperar(() => invocaciones == 2);
        liberarPrimera.SetResult();                                                  // termina la vieja: no debe retirar a la nueva
        await Task.Delay(50);
        var c = deduplicador.EjecutarAsync("k", Fabrica, CancellationToken.None);   // debe unirse a la nueva, no crear una tercera
        liberarSegunda.SetResult();

        Assert.Equal("segunda", await b);
        Assert.Equal("segunda", await c);
        Assert.Equal(2, invocaciones);
        Assert.Equal(0, deduplicador.EnVuelo);
    }

    [Fact]
    public async Task Cada_llamador_espera_con_su_propio_token_y_la_operacion_sigue_mientras_quede_alguno()
    {
        var deduplicador = new Deduplicador<string, string>();
        var puerta = new TaskCompletionSource();
        var canceladaOperacion = false;

        async Task<string> Fabrica(CancellationToken ct)
        {
            ct.Register(() => canceladaOperacion = true);
            await puerta.Task.WaitAsync(ct);
            return "ok";
        }

        using var a = new CancellationTokenSource();
        var ta = deduplicador.EjecutarAsync("k", Fabrica, a.Token);
        var tb = deduplicador.EjecutarAsync("k", Fabrica, CancellationToken.None);
        await Esperar(() => deduplicador.EnVuelo == 1);
        await a.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ta);
        puerta.SetResult();

        Assert.Equal("ok", await tb);
        Assert.False(canceladaOperacion);
    }

    [Fact]
    public async Task La_cancelacion_adicional_de_un_llamador_lo_libera_sin_esperar_a_la_operacion_y_sin_cancelarla_para_otros()
    {
        var deduplicador = new Deduplicador<string, string>();
        var puerta = new TaskCompletionSource();

        Task<string> Fabrica(CancellationToken ct) => puerta.Task.WaitAsync(ct).ContinueWith(_ => "ok", TaskScheduler.Default);

        using var presupuesto = new CancellationTokenSource();
        var conPresupuesto = deduplicador.EjecutarAsync("k", Fabrica, CancellationToken.None, presupuesto.Token);
        var sinPresupuesto = deduplicador.EjecutarAsync("k", Fabrica, CancellationToken.None);
        await Esperar(() => deduplicador.EnVuelo == 1);
        await presupuesto.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conPresupuesto.WaitAsync(TimeSpan.FromSeconds(5)));   // no queda atado a la operación
        Assert.False(sinPresupuesto.IsCompleted);
        puerta.SetResult();
        Assert.Equal("ok", await sinPresupuesto);
    }

    [Fact]
    public async Task Un_error_de_la_operacion_llega_a_todos_los_que_esperan_y_la_clave_queda_libre()
    {
        var deduplicador = new Deduplicador<string, string>();
        var puerta = new TaskCompletionSource();
        var invocaciones = 0;

        async Task<string> Fabrica(CancellationToken ct)
        {
            Interlocked.Increment(ref invocaciones);
            await puerta.Task;
            throw new InvalidOperationException("falló");
        }

        var a = deduplicador.EjecutarAsync("k", Fabrica, CancellationToken.None);
        var b = deduplicador.EjecutarAsync("k", Fabrica, CancellationToken.None);
        await Esperar(() => invocaciones == 1);
        puerta.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => a);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b);
        Assert.Equal(1, invocaciones);
        Assert.Equal(0, deduplicador.EnVuelo);
    }

    [Fact]
    public async Task Claves_distintas_no_comparten_operacion()
    {
        var deduplicador = new Deduplicador<string, string>();
        var invocaciones = 0;
        Task<string> Fabrica(CancellationToken ct) { Interlocked.Increment(ref invocaciones); return Task.FromResult("x"); }

        await Task.WhenAll(deduplicador.EjecutarAsync("a", Fabrica, CancellationToken.None), deduplicador.EjecutarAsync("b", Fabrica, CancellationToken.None));

        Assert.Equal(2, invocaciones);
    }
}

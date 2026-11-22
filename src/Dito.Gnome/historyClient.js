import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

Gio._promisify(Gio.OutputStream.prototype, 'write_all_async');
Gio._promisify(Gio.DataInputStream.prototype, 'read_line_async');
Gio._promisify(Gio.Subprocess.prototype, 'wait_async');

export class HistoryClient {
    constructor(path) {
        this._path = path;
        this._closed = false;
        this._pending = 0;
        this._generation = 0;
        this._tail = Promise.resolve();
    }

    send(request, isCurrent = () => true) {
        if (this._closed || this._pending >= 64)
            return Promise.reject(new Error('History service unavailable'));

        this._pending++;
        const generation = this._generation;
        const result = this._tail.then(() => {
            if (!isCurrent() || (this._closed && request.operation !== 'capture'))
                return null;
            if (generation !== this._generation)
                throw new Error('History service disconnected');
            return this._exchangeAsync(request);
        });
        this._tail = result.catch(() => {}).finally(() => this._pending--);
        return result;
    }

    close() {
        this._closed = true;
        void this._tail.then(async () => {
            this._input?.close(null);
            if (this._process)
                await this._process.wait_async(null);
            this._output?.close(null);
        }).catch(() => this._stop());
    }

    _start() {
        this._process = Gio.Subprocess.new([this._path],
            Gio.SubprocessFlags.STDIN_PIPE | Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
        this._input = this._process.get_stdin_pipe();
        this._output = new Gio.DataInputStream({base_stream: this._process.get_stdout_pipe()});
    }

    _stop() {
        this._generation++;
        this._process?.force_exit();
        this._process = null;
        this._input = null;
        this._output = null;
    }

    async _exchangeAsync(request) {
        const cancellation = new Gio.Cancellable();
        const timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 8000, () => {
            cancellation.cancel();
            return GLib.SOURCE_CONTINUE;
        });
        let response;
        try {
            if (!this._process)
                this._start();

            const input = new TextEncoder().encode(`${JSON.stringify(request)}\n`);
            await this._input.write_all_async(input, GLib.PRIORITY_DEFAULT, cancellation);
            const [output] = await this._output.read_line_async(GLib.PRIORITY_DEFAULT, cancellation);
            if (!output)
                throw new Error('History service disconnected');

            response = JSON.parse(new TextDecoder().decode(output));
        } catch (error) {
            this._stop();
            throw error;
        } finally {
            GLib.Source.remove(timer);
        }

        if (!response.ok)
            throw new Error('History operation failed');
        return response.result;
    }
}

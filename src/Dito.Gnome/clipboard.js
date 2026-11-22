import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';

const selectionType = Meta.SelectionType.SELECTION_CLIPBOARD;
const textMime = 'text/plain;charset=utf-8';
const textFormats = [textMime, 'UTF8_STRING', 'text/plain'];

Gio._promisify(Meta.Selection.prototype, 'transfer_async', 'transfer_finish');

export class ClipboardCapture {
    constructor(capture, failed) {
        this._selection = global.display.get_selection();
        this._owners = new WeakSet();
        this._capture = capture;
        this._failed = failed;
        this._paused = false;
        this._changed = this._selection.connect('owner-changed',
            (_selection, type, owner) => this._onOwnerChanged(type, owner));
    }

    get paused() {
        return this._paused;
    }

    setPaused(paused) {
        this._paused = paused;
        this._cancelRead();
    }

    copy(text) {
        const owner = Meta.SelectionSourceMemory.new(textMime, new GLib.Bytes(new TextEncoder().encode(text)));
        this._owners.add(owner);
        this._selection.set_owner(selectionType, owner);
    }

    destroy() {
        this.setPaused(true);
        this._selection.disconnect(this._changed);
    }

    _onOwnerChanged(type, owner) {
        if (type !== selectionType)
            return;

        this._cancelRead();
        if (this._paused || !owner || this._owners.has(owner))
            return;

        const formats = owner.get_mimetypes();
        const mime = textFormats.find(candidate => formats.includes(candidate));
        if (!mime)
            return;

        const operation = new Gio.Cancellable();
        this._reading = operation;
        void this._readAsync(mime, Date.now(), operation);
    }

    async _readAsync(mime, copiedAt, operation) {
        try {
            const output = Gio.MemoryOutputStream.new_resizable();
            const transferred = await this._selection.transfer_async(selectionType, mime, -1, output, operation);
            if (!transferred || operation.is_cancelled())
                return;

            output.close(null);
            const data = output.steal_as_bytes().get_data();
            if (data.length === 0)
                return;

            const text = new TextDecoder('utf-8', {fatal: true, ignoreBOM: true}).decode(data);
            await this._capture(text, copiedAt);
        } catch {
            if (!operation.is_cancelled())
                this._failed();
        } finally {
            if (this._reading === operation)
                this._reading = null;
        }
    }

    _cancelRead() {
        this._reading?.cancel();
        this._reading = null;
    }
}

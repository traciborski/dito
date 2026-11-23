import Clutter from 'gi://Clutter';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import Pango from 'gi://Pango';
import St from 'gi://St';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';

import {ClipboardCapture} from './clipboard.js';
import {HistoryClient} from './historyClient.js';

export default class DitoExtension extends Extension {
    enable() {
        if (!global.context.get_wayland_compositor())
            return;

        this._history = new HistoryClient(GLib.build_filenamev([this.path, 'history', 'Dito.Gnome.History']));
        this._menu = new HistoryMenu(this._history,
            text => this._clipboard.copy(text),
            paused => this._clipboard.setPaused(paused));
        this._clipboard = new ClipboardCapture((text, copiedAt) =>
            this._history.send({operation: 'capture', text, copiedAt}), () => this._menu?.captureFailed());
        Main.panel.addToStatusArea(this.uuid, this._menu);
    }

    disable() {
        this._clipboard?.destroy();
        this._menu?.destroy();
        this._history?.close();
        this._clipboard = null;
        this._menu = null;
        this._history = null;
    }
}

function addContent(menu, actor) {
    const item = new PopupMenu.PopupBaseMenuItem({reactive: false, can_focus: false});
    item.add_child(actor);
    menu.addMenuItem(item);
}

function scrolling(child, height) {
    const scroll = new St.ScrollView({
        x_expand: true,
        style: `max-height: ${height}px;`,
        hscrollbar_policy: St.PolicyType.NEVER,
        vscrollbar_policy: St.PolicyType.AUTOMATIC,
    });
    scroll.set_child(child);
    return scroll;
}

function button(label, clicked) {
    const actor = new St.Button({label, style_class: 'button', can_focus: true});
    actor.connect('clicked', clicked);
    return actor;
}

const HistoryMenu = GObject.registerClass(class DitoHistoryMenu extends PanelMenu.Button {
    _init(history, copy, pause) {
        super._init(0.0, 'Dito');
        this._history = history;
        this._copy = copy;
        this._pause = pause;
        this._paused = false;
        this._active = true;
        this._revision = 0;
        this._previewRevision = 0;
        this._timer = 0;
        this._cursorStack = [null];
        this._rows = [];
        this._selected = -1;
        this.add_child(new St.Icon({icon_name: 'edit-paste-symbolic', style_class: 'system-status-icon'}));
        this._buildMenu();

        this.menu.connect('open-state-changed', (_menu, open) => {
            this._invalidate();
            this._deleteHeld = false;
            if (open) {
                this._entry.set_text('');
                this._entry.grab_key_focus();
                this._queueSearch(false);
            }
        });
    }

    _buildMenu() {
        this._entry = new St.Entry({hint_text: 'Search clipboard history', can_focus: true, x_expand: true,
            style: 'min-width: 420px;'});
        this._entry.clutter_text.connect('text-changed', () => this._queueSearch(true));
        this._entry.clutter_text.connect('key-press-event', (_entry, event) => this._onKey(event));
        addContent(this.menu, this._entry);
        this.menu.actor.connect('captured-event', (_actor, event) => this._onDeleteKey(event));

        this._results = new St.BoxLayout({vertical: true, x_expand: true});
        this._list = scrolling(this._results, 240);
        addContent(this.menu, this._list);
        this._preview = new St.Label({x_expand: true, style: 'max-width: 420px;'});
        this._preview.clutter_text.line_wrap = true;
        const previewBox = new St.BoxLayout({vertical: true, x_expand: true});
        previewBox.add_child(this._preview);
        addContent(this.menu, scrolling(previewBox, 140));
        this._label = new St.Entry({hint_text: 'Optional label', can_focus: true, x_expand: true});
        this._label.clutter_text.activatable = true;
        this._label.clutter_text.connect('activate', () => this._saveLabel());
        this._saveLabelButton = button('Save label', () => this._saveLabel());
        this._deleteButton = button('Delete entry', () => void this._changeSelectedAsync('delete'));
        const entryActions = new St.BoxLayout({style: 'spacing: 6px;'});
        for (const control of [this._label, this._saveLabelButton, this._deleteButton])
            entryActions.add_child(control);
        addContent(this.menu, entryActions);
        this._status = new St.Label({x_expand: true});
        addContent(this.menu, this._status);

        this._newer = button('Newer', () => this._page(-1));
        this._older = button('Older', () => this._page(1));
        this._copyButton = button('Copy', () => void this._copySelected());
        this._pauseButton = button('Pause Capture', () => this._setPaused(!this._paused));
        const controls = new St.BoxLayout({style: 'spacing: 6px;'});
        for (const control of [this._newer, this._older, this._copyButton, this._pauseButton])
            controls.add_child(control);
        addContent(this.menu, controls);
    }

    captureFailed() {
        this._setPaused(true);
        this._status.text = 'Capture paused. Check clipboard and history access.';
    }

    destroy() {
        this._active = false;
        this._invalidate();
        super.destroy();
    }

    _setPaused(paused) {
        this._paused = paused;
        this._pause(paused);
        this._pauseButton.label = paused ? 'Resume Capture' : 'Pause Capture';
    }

    _invalidate() {
        this._revision++;
        this._previewRevision++;
        if (this._timer) {
            GLib.Source.remove(this._timer);
            this._timer = 0;
        }
    }

    _queueSearch(debounce) {
        if (!this._active || !this.menu.isOpen)
            return;

        this._cursorStack = [null];
        this._beginPage(debounce);
    }

    _beginPage(debounce) {
        this._invalidate();
        this._results.destroy_all_children();
        this._rows = [];
        this._selected = -1;
        this._preview.text = '';
        this._label.set_text('');
        this._next = null;
        this._buttons(false);
        this._status.text = 'Searching...';
        const revision = this._revision;
        if (debounce) {
            this._timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 150, () => {
                this._timer = 0;
                void this._loadPageAsync(revision);
                return GLib.SOURCE_REMOVE;
            });
        } else {
            void this._loadPageAsync(revision);
        }
    }

    async _loadPageAsync(revision) {
        const query = this._entry.get_text();
        try {
            const page = await this._history.send({operation: 'search', query, cursor: this._cursorStack.at(-1)},
                () => revision === this._revision);
            if (revision !== this._revision)
                return;

            this._next = page.nextCursor;
            this._rows = page.items.map((entry, index) => {
                const preview = entry.label ? `[${entry.label}] ${entry.preview}` : entry.preview;
                const label = new St.Label({text: preview.replace(/[\r\n\t\0]/g, ' ')});
                label.clutter_text.ellipsize = Pango.EllipsizeMode.END;
                const row = new St.Button({child: label, x_expand: true, style_class: 'button', can_focus: true});
                row.connect('clicked', () => this._select(index));
                this._results.add_child(row);
                return {id: entry.id, label: entry.label, row};
            });
            this._status.text = this._rows.length ? 'Enter copies selected text. Paste it in your other app.'
                : query.trim() ? 'No matching entries.' : 'Clipboard history is empty.';
            this._buttons(true);
            if (this._rows.length)
                this._select(0);
        } catch {
            if (revision === this._revision)
                this._status.text = 'History could not be searched. Check history access.';
        }
    }

    _buttons(loaded) {
        this._newer.reactive = loaded && this._cursorStack.length > 1;
        this._older.reactive = loaded && this._next !== null;
        const hasEntries = loaded && this._rows.length > 0;
        this._copyButton.reactive = hasEntries;
        this._label.clutter_text.editable = hasEntries;
        this._saveLabelButton.reactive = hasEntries;
        this._deleteButton.reactive = hasEntries;
    }

    _page(direction) {
        if (direction < 0 && this._newer.reactive)
            this._cursorStack.pop();
        else if (direction > 0 && this._older.reactive)
            this._cursorStack.push(this._next);
        else
            return;

        this._beginPage(false);
    }

    _select(index) {
        if (index < 0 || index >= this._rows.length)
            return;

        this._rows[this._selected]?.row.remove_style_pseudo_class('selected');
        this._selected = index;
        const {id, label, row} = this._rows[index];
        this._label.set_text(label ?? '');
        row.add_style_pseudo_class('selected');
        const adjustment = this._list.vadjustment;
        adjustment.clamp_page(row.y, row.y + row.height);
        this._preview.text = '';
        const revision = ++this._previewRevision;
        void this._loadPreviewAsync(id, revision);
    }

    async _loadPreviewAsync(id, revision) {
        try {
            const text = await this._history.send({operation: 'read', id},
                () => revision === this._previewRevision);
            if (revision === this._previewRevision)
                this._preview.text = (text ?? '').replaceAll('\0', '\u2400');
        } catch {
            if (revision === this._previewRevision)
                this._status.text = 'The selected text could not be loaded.';
        }
    }

    async _copySelected() {
        const entry = this._rows[this._selected];
        if (!entry || this._recalling || this._changing)
            return;

        const revision = this._previewRevision;
        this._recalling = true;
        try {
            const text = await this._history.send({operation: 'read', id: entry.id},
                () => revision === this._previewRevision);
            if (revision === this._previewRevision && text !== null) {
                this._copy(text);
                this.menu.close();
            }
        } catch {
            if (revision === this._previewRevision)
                this._status.text = 'The selected text could not be copied.';
        } finally {
            this._recalling = false;
        }
    }

    _saveLabel() {
        void this._changeSelectedAsync('setLabel', this._label.get_text());
    }

    async _changeSelectedAsync(operation, label) {
        const entry = this._rows[this._selected];
        if (!entry || this._recalling || this._changing)
            return;

        const revision = this._revision;
        this._changing = true;
        this._entry.grab_key_focus();
        try {
            await this._history.send({operation, id: entry.id, label});
            if (revision === this._revision)
                this._queueSearch(false);
        } catch {
            if (revision === this._revision) {
                this._status.text = operation === 'delete'
                    ? 'The entry could not be deleted. Check history access.'
                    : 'The label could not be saved. Check history access.';
            }
        } finally {
            this._changing = false;
        }
    }

    _onDeleteKey(event) {
        const type = event.type();
        if ((type !== Clutter.EventType.KEY_PRESS && type !== Clutter.EventType.KEY_RELEASE) ||
            event.get_key_symbol() !== Clutter.KEY_Delete)
            return Clutter.EVENT_PROPAGATE;

        if (type === Clutter.EventType.KEY_RELEASE) {
            this._deleteHeld = false;
            return Clutter.EVENT_PROPAGATE;
        }

        const modifiers = Clutter.ModifierType.CONTROL_MASK | Clutter.ModifierType.SHIFT_MASK |
            Clutter.ModifierType.MOD1_MASK | Clutter.ModifierType.MOD4_MASK | Clutter.ModifierType.SUPER_MASK;
        if (global.stage.get_key_focus() === this._label.clutter_text || (event.get_state() & modifiers))
            return Clutter.EVENT_PROPAGATE;
        if (!this._deleteHeld && !this._rows[this._selected])
            return Clutter.EVENT_PROPAGATE;

        if (!this._deleteHeld) {
            this._deleteHeld = true;
            void this._changeSelectedAsync('delete');
        }
        return Clutter.EVENT_STOP;
    }

    _onKey(event) {
        switch (event.get_key_symbol()) {
        case Clutter.KEY_Up:
            this._select(Math.max(0, this._selected - 1));
            break;
        case Clutter.KEY_Down:
            this._select(Math.min(this._rows.length - 1, this._selected + 1));
            break;
        case Clutter.KEY_Page_Up:
            this._page(-1);
            break;
        case Clutter.KEY_Page_Down:
            this._page(1);
            break;
        case Clutter.KEY_Return:
        case Clutter.KEY_KP_Enter:
            void this._copySelected();
            break;
        default:
            return Clutter.EVENT_PROPAGATE;
        }

        return Clutter.EVENT_STOP;
    }
});

import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {sleep} from 'resource:///org/gnome/shell/ui/scripting.js';

const uuid = 'dito@traciborski.github.io';
const selection = Meta.SelectionType.SELECTION_CLIPBOARD;
const mime = 'text/plain;charset=utf-8';

function check(condition, message) {
    if (!condition)
        throw new Error(message);
}

async function until(checkCondition, description) {
    for (let attempt = 0; attempt < 300; attempt++) {
        if (await checkCondition())
            return;
        await sleep(50);
    }
    throw new Error(`Timed out: ${description}`);
}

function setSyntheticClipboard(text) {
    const bytes = new GLib.Bytes(new TextEncoder().encode(text));
    global.display.get_selection().set_owner(selection, Meta.SelectionSourceMemory.new(mime, bytes));
}

function readSyntheticClipboard() {
    return new Promise(resolve => St.Clipboard.get_default().get_content(St.ClipboardType.CLIPBOARD, mime,
        (_clipboard, bytes) => resolve(bytes ? new TextDecoder().decode(bytes.get_data()) : null)));
}

export async function run() {
    check(GLib.getenv('XDG_DATA_HOME')?.startsWith('/tmp/'), 'Run only in the isolated Shell test tool');
    check(GLib.getenv('DITO_GNOME_TEST') === '1', 'Run only in the isolated Shell test tool');
    await until(() => Main.extensionManager.lookup(uuid)?.stateObj?._menu, 'extension activation');
    const extension = Main.extensionManager.lookup(uuid).stateObj;
    const { _history: history, _menu: menu } = extension;
    const text = 'project caf\u00e9\r\nfirst line\0last line';
    const readPage = (query = '') => history.send({operation: 'search', query});
    await history.send({operation: 'capture', text: ' \u00a0\t\r\n', copiedAt: Date.now()});
    check((await readPage()).items.length === 0, 'Whitespace-only capture was saved');
    setSyntheticClipboard(`  ${text}\t `);
    await until(async () => (await readPage()).items.length === 1, 'first capture');
    setSyntheticClipboard('newer synthetic clip');
    await until(async () => (await readPage()).items.length === 2, 'second capture');

    menu.menu.open();
    menu._entry.set_text('project caf');
    check((await readPage('project caf')).items.length === 1, 'History search did not match the synthetic text');
    try {
        await until(() => menu._rows.length === 1 && menu._preview.text.includes('first line'), 'search and preview');
    } catch (error) {
        throw new Error(`Popup had ${menu._rows.length} rows; status: ${menu._status.text}`, {cause: error});
    }
    const id = menu._rows[0].id;
    check(await history.send({operation: 'read', id}) === text, 'Capture did not trim just the surrounding whitespace');
    menu._label.set_text('Example email');
    menu._saveLabel();
    await until(async () => (await readPage('example ema')).items[0]?.id === id, 'save searchable label');
    menu._entry.set_text('example ema');
    await until(() => menu._rows[0]?.id === id && menu._preview.text.includes('first line'), 'search by label');
    check((await readPage()).items[0].id !== id, 'Preview changed history recency');
    await menu._copySelected();
    check(!menu.menu.isOpen, 'Recall did not close the popup');
    check(await readSyntheticClipboard() === text, 'Recall did not offer exact clipboard text');
    await sleep(250);
    const recalled = (await readPage()).items;
    check(recalled[0].id === id, 'Recall did not move the entry to the top');
    check(recalled.length === 2, 'Recall duplicated an entry');
    check(recalled[0].label === 'Example email', 'Recall discarded the label');

    menu._setPaused(true);
    setSyntheticClipboard('must not be captured');
    await sleep(250);
    check((await readPage()).items.length === 2, 'Paused capture saved a copy');
    menu._setPaused(false);
    setSyntheticClipboard('fresh after resuming');
    await until(async () => (await readPage()).items.length === 3, 'resume capture');
    setSyntheticClipboard(text);
    await until(async () => (await readPage()).items[0].id === id, 'external recopy updates recency');
    check((await readPage()).items.length === 3, 'External recopy duplicated an entry');
    check((await readPage('example ema')).items[0]?.id === id, 'Recopy discarded the label');

    const clipboardClient = Gio.SubprocessLauncher.new(Gio.SubprocessFlags.STDIN_PIPE);
    clipboardClient.setenv('WAYLAND_DISPLAY', 'gnome-shell-test-display', true);
    clipboardClient.setenv('GDK_BACKEND', 'wayland', true);
    clipboardClient.setenv('GTK_A11Y', 'none', true);
    Main.overview.hide();
    await until(() => !Main.overview.visible, 'close test overview');
    const nativeCopy = clipboardClient.spawnv(['python3', GLib.getenv('DITO_GNOME_NATIVE_COPY')]);
    try {
        await until(() => global.get_window_actors().some(actor =>
            actor.meta_window.get_title() === 'Dito GNOME test'), 'Wayland test window');
        const testWindow = global.get_window_actors().find(actor =>
            actor.meta_window.get_title() === 'Dito GNOME test').meta_window;
        global.stage.set_key_focus(null);
        Main.activateWindow(testWindow);
        await until(() => global.display.focus_window === testWindow, 'Wayland test window focus');
        nativeCopy.get_stdin_pipe().write_all(new TextEncoder().encode('copy\n'), null);
        await until(async () => (await readPage('native Wayland')).items.length === 1, 'native Wayland copy');
    } finally {
        nativeCopy.force_exit();
    }

    extension.disable();
    extension.enable();
    await until(async () => (await extension._history.send({operation: 'search', query: ''})).items.length === 4,
        'history persisted after restarting extension');
    const reopened = extension._menu;
    reopened.menu.open();
    reopened._entry.set_text('example ema');
    await until(() => reopened._rows[0]?.id === id, 'label persisted after restarting extension');
    const deletePress = {
        type: () => Clutter.EventType.KEY_PRESS,
        get_key_symbol: () => Clutter.KEY_Delete,
        get_state: () => 0,
    };
    check(reopened._onDeleteKey(deletePress) === Clutter.EVENT_STOP, 'Delete passed through to the search box');
    await until(() => reopened._status.text === 'No matching entries.', 'Delete removed the selected result');
    check(await extension._history.send({operation: 'read', id}) === null, 'Deleted entry could still be recalled');
    extension.disable();
    console.log('Dito GNOME smoke test passed.');
}

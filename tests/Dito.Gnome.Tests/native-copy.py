import sys

import gi

gi.require_version("Gtk", "4.0")
from gi.repository import GLib, Gtk


def activate(app):
    window = Gtk.ApplicationWindow(application=app, title="Dito GNOME test")

    def copy(*_):
        if not sys.stdin.readline():
            return GLib.SOURCE_REMOVE
        window.get_clipboard().set("text from a native Wayland client")
        return GLib.SOURCE_REMOVE

    window.present()
    GLib.io_add_watch(sys.stdin, GLib.PRIORITY_DEFAULT, GLib.IO_IN | GLib.IO_HUP, copy)


app = Gtk.Application(application_id="io.github.traciborski.Dito.Test")
app.connect("activate", activate)
app.run()

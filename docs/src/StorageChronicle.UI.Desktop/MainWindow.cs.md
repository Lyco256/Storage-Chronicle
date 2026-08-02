# MainWindow.cs

Hosts the top navigation shell. It must not access the file system, SQLite, or Windows APIs directly. Headless startup tests verify that it can run with fake data.

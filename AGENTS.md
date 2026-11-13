# Website project boundaries

This repository owns the website and its offline map extractor. Website source, builds, packages, installation, configuration, and runtime dependencies are independent from Lobby2 and Reforger MCP.

Do not link source files from sibling projects or bundle MCP into the website. Optional developer tools may be used through an explicitly defined API; they are not application dependencies. Existing API data contracts are allowed.

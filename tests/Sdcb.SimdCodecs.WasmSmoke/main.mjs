// Runs the published app under Node.js: `node main.mjs` from the publish directory's wwwroot.
import { dotnet } from './_framework/dotnet.js';

const exitCode = await dotnet.run();
process.exit(exitCode);

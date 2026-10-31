import { dotnet } from './_framework/dotnet.js'

const { runMain } = await dotnet.withDiagnosticTracing(false).create();
const code = await runMain();
process.exit(code);

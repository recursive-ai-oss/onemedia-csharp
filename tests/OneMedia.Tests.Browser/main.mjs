// Starts the suite under node (arguments from the command line) or in a browser page (arguments from ?args=a+b in the address).
// tools/wasm-suite/run.py hosts the bundle in headless Chromium.
import { dotnet } from './_framework/dotnet.js'

const inNode = typeof process !== 'undefined' && !!process.versions?.node;
const args = inNode
  ? process.argv.slice(2)
  : (new URLSearchParams(globalThis.location.search).get('args') ?? '').split(' ').filter(a => a.length > 0);

const { runMain } = await dotnet.withDiagnosticTracing(false).withApplicationArguments(...args).create();
const code = await runMain();
if (inNode) {
  process.exit(code);
} else {
  console.log('EXIT ' + code);
}

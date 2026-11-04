// Stands in for the real CLIs. The reply depends on the prompt so tests can pick a scenario.
const fs = require('node:fs');

const args = process.argv.slice(2);
if (process.env.FAKE_ARGS_FILE) fs.writeFileSync(process.env.FAKE_ARGS_FILE, JSON.stringify(args));

const out = (o) => process.stdout.write(JSON.stringify(o) + '\n');

function claude(prompt) {
  if (prompt.includes('SCENARIO_MISSING')) {
    // Matches the real CLI: an empty error result on stdout, the reason on stderr.
    out({ type: 'result', subtype: 'error_during_execution', is_error: true });
    process.stderr.write('No conversation found with session ID: abc\n');
    process.exitCode = 1;
    return;
  }
  if (prompt.includes('SCENARIO_AUTH')) {
    process.stderr.write('Not logged in. Please run /login\n');
    process.exit(1);
  }
  out({ type: 'system', subtype: 'init', session_id: 'session-1' });
  if (prompt.includes('SCENARIO_LIMIT')) {
    out({ type: 'result', is_error: true, result: "You've hit your usage limit." });
    return;
  }
  if (prompt.includes('SCENARIO_HANG')) {
    out({ type: 'stream_event', event: { delta: { type: 'text_delta', text: 'partial' } } });
    setInterval(() => {}, 1000);
    return;
  }
  out({ type: 'stream_event', event: { delta: { type: 'text_delta', text: 'Hel' } } });
  out({ type: 'stream_event', event: { delta: { type: 'thinking_delta', thinking: 'hidden' } } });
  out({ type: 'stream_event', event: { delta: { type: 'text_delta', text: 'lo' } } });
  out({
    type: 'rate_limit_event',
    rate_limit_info: { unifiedWindows: { five_hour: { utilization: 0.14, resetsAt: 100 }, seven_day: { utilization: 0.34 } } },
  });
  out({ type: 'result', is_error: false, result: 'Hello' });
}

let input = '';
process.stdin.on('data', (d) => (input += d));
process.stdin.on('end', () => claude(input));

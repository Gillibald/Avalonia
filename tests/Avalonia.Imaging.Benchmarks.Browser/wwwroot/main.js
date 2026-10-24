import { dotnet } from './_framework/dotnet.js';

const log = document.getElementById('log');
const args = [...new URLSearchParams(location.search).entries()]
    .map(([key, value]) => value ? `${key}=${value}` : key)
    .concat([`base=${new URL('.', location.href)}`]);
const { setModuleImports, runMain } = await dotnet.create();

setModuleImports('main.js', {
    log: (text) => { log.textContent += '\n' + text; console.log(text); },
    result: (markdown, json) => {
        document.getElementById('markdown').textContent = markdown;
        document.getElementById('json').textContent = json;
        document.title = 'Imaging benchmarks (done)';
        // ?save posts the report to the serving host, for scripted runs.
        if (new URLSearchParams(location.search).has('save')) {
            fetch('results/browser.json', { method: 'POST', body: json });
            fetch('results/browser.md', { method: 'POST', body: markdown });
        }
    },
});

// Yield once so the page paints before the long synchronous run starts.
await new Promise((resolve) => setTimeout(resolve, 0));
await runMain('Avalonia.Imaging.Benchmarks.Browser', args);


const fs = require('fs');
const path = require('path');

// Load the understand-anything core
const understandPath = path.resolve('C:/Users/Lugia/Understand-Anything/understand-anything-plugin/packages/core/dist/index.js');
const analyze = require(understandPath);

const filePath = 'C:/Users/Lugia/Downloads/KitLugia-master (25) - Copia - Copia - Copia - Copia/KitLugia-master - Copia - Copia/KitLugia.Core/DeepUninstaller.cs';
const content = fs.readFileSync(filePath, 'utf8');

try {
    const result = analyze.file(filePath, content);
    console.log(JSON.stringify(result, null, 2));
} catch (e) {
    console.error('Error:', e.message);
    process.exit(1);
}

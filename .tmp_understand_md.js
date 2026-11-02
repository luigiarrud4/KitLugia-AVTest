
const fs = require('fs');
const path = require('path');

const MarkdownParser = require('C:/Users/Lugia/Understand-Anything/understand-anything-plugin/packages/core/dist/MarkdownParser');
const parser = new MarkdownParser();

const docPath = 'C:/Users/Lugia/Downloads/KitLugia-master (25) - Copia - Copia - Copia - Copia/KitLugia-master - Copia - Copia/docs/HUNTER_AUDIT.md';
const content = fs.readFileSync(docPath, 'utf8');

try {
    const result = parser.parse(content, docPath);
    console.log(JSON.stringify({
        headings: result.headings?.length || 0,
        links: result.links?.length || 0,
        sections: result.sections?.length || 0,
        preview: result.headings?.slice(0, 5) || []
    }, null, 2));
} catch (e) {
    console.error('Error:', e.message);
    process.exit(1);
}

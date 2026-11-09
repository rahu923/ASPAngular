// One-time AST-based extraction. Run `node scripts/Extract-AngularTemplates.cjs`.
const fs = require('node:fs');
const path = require('node:path');
const ts = require('../frontend/node_modules/typescript');
const root = path.resolve(__dirname, '../frontend/src/app');
let count = 0;
function visitFile(file) {
  const source = fs.readFileSync(file, 'utf8');
  const tree = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
  const edits = [];
  function visit(node) {
    if (ts.isClassDeclaration(node)) {
      for (const decorator of ts.getDecorators(node) || []) {
        if (!ts.isCallExpression(decorator.expression) || decorator.expression.expression.getText(tree) !== 'Component') continue;
        const metadata = decorator.expression.arguments[0];
        if (!metadata || !ts.isObjectLiteralExpression(metadata)) continue;
        const template = metadata.properties.find(p => ts.isPropertyAssignment(p) && p.name.getText(tree) === 'template');
        if (!template) continue;
        if (!ts.isStringLiteral(template.initializer) && !ts.isNoSubstitutionTemplateLiteral(template.initializer)) throw new Error(`Dynamic template in ${file}`);
        const name = node.name.text.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase() + '.component.html';
        const destination = path.join(path.dirname(file), name);
        if (fs.existsSync(destination)) throw new Error(`Refusing to overwrite ${destination}`);
        fs.writeFileSync(destination, template.initializer.text.trim() + '\n');
        edits.push({ start: template.getStart(tree), end: template.end, text: `templateUrl: './${name}'` });
        count++;
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(tree);
  let result = source;
  for (const edit of edits.sort((a,b) => b.start - a.start)) result = result.slice(0,edit.start) + edit.text + result.slice(edit.end);
  if (edits.length) fs.writeFileSync(file, result);
}
function walk(directory) {
  for (const item of fs.readdirSync(directory, { withFileTypes: true })) {
    const file = path.join(directory, item.name);
    if (item.isDirectory()) walk(file);
    else if (file.endsWith('.ts') && !file.endsWith('.spec.ts')) visitFile(file);
  }
}
walk(root);
console.log(`Extracted ${count} component templates.`);

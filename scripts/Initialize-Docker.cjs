const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const destination = path.resolve(__dirname, '../.env');
if (!fs.existsSync(destination)) {
  const random = () => crypto.randomBytes(32).toString('hex');
  fs.writeFileSync(destination, [
    `SQL_PASSWORD=Sql!7Aa${random()}`, `JWT_SIGNING_KEY=${random()}${random()}`,
    `DEVELOPMENT_PASSWORD=Practice!7Aa${random()}`, `SEQ_PASSWORD=Seq!7Aa${random()}`,
    `PFX_PASSWORD=${random()}`, ''
  ].join('\n'), { mode: 0o600 });
}
console.log('Docker secrets are in ignored .env. No existing values were changed.');

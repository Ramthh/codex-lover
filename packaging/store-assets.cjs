const sharp = require('sharp');
const path = require('node:path');
const fs = require('node:fs/promises');
const output = process.argv[2];
const source = path.join(__dirname, 'assets', 'favicon.svg');
(async () => {
  await fs.mkdir(output, { recursive: true });
  for (const [name, size] of [['StoreLogo',50],['Square44x44Logo',44],['Square71x71Logo',71],['Square150x150Logo',150],['Square310x310Logo',310]]) {
    await sharp(source).resize(size,size).png().toFile(path.join(output,name+'.png'));
  }
  const logo = await sharp(source).resize(120,120).png().toBuffer();
  await sharp({create:{width:310,height:150,channels:4,background:'#f4f1ff'}}).composite([{input:logo,gravity:'center'}]).png().toFile(path.join(output,'Wide310x150Logo.png'));
})();

import { promises as fsp } from 'node:fs'
import convert from 'xml-js'

(async () => {

    // return data.replace(/(<Version>)(.*)(<\/Version>)/, `$1${newVersion}$3`)
})();

console.log(process.cwd());
const data = await fsp.readFile('./../../../mod/JunimoServer/JunimoServer.csproj', 'utf-8');

const newVersion = '1337';



try {
    const parsed = convert.xml2js(data, { captureSpacesBetweenElements: true });

    const project = parsed.elements[0];
    const propertyGroup = project.elements.find(item => item.name === 'Prop2ertyGroup');
    const version = propertyGroup.elements.find(item => item.name === 'Version');

    console.log(version);
    version.elements[0].text = newVersion;
    console.log(version);

    console.log(convert.js2xml(parsed));

} catch (err) {
    console.error(err, '\nFailed updating xml.');
}

"""Exercise the actual Store stamping script on disposable fixture files only."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PS = shutil.which('powershell') or shutil.which('pwsh')
NS = '{http://schemas.microsoft.com/appx/manifest/foundation/windows10}'


@unittest.skipUnless(PS, 'PowerShell required for the Windows packaging script')
class StoreVersionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='replysis-version-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'InterviewCopilotPackager').mkdir()
        self.manifest = self.root / 'InterviewCopilotPackager/Package.appxmanifest'
        self.project = self.root / 'InterviewCopilot.csproj'
        shutil.copyfile(ROOT / 'InterviewCopilotPackager/Package.appxmanifest', self.manifest)
        shutil.copyfile(ROOT / 'InterviewCopilot.csproj', self.project)

    def stamp(self, version):
        return subprocess.run([PS, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                               str(ROOT / 'tools/set-store-version.ps1'), '-Version', version,
                               '-ProjectRoot', str(self.root)], capture_output=True, text=True, timeout=20)

    def test_identity_and_app_version_match_without_changing_windows_requirements(self):
        before = [dict(n.attrib) for n in ET.parse(self.manifest).iter(NS + 'TargetDeviceFamily')]
        result = self.stamp('9.8.7.0')
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest = ET.parse(self.manifest)
        self.assertEqual(manifest.find(NS + 'Identity').get('Version'), '9.8.7.0')
        self.assertEqual([dict(n.attrib) for n in manifest.iter(NS + 'TargetDeviceFamily')], before)
        project = ET.parse(self.project)
        for name in ('Version', 'AssemblyVersion', 'FileVersion'):
            self.assertEqual(project.find('./PropertyGroup/' + name).text, '9.8.7.0')

    def test_bad_versions_do_not_change_files(self):
        original = (self.manifest.read_bytes(), self.project.read_bytes())
        for version in ('1.2.3', '1.2.3.4', '1.2.999999.0', 'hello', '1.2.3.0;whoami'):
            with self.subTest(version=version):
                self.assertNotEqual(self.stamp(version).returncode, 0)
                self.assertEqual((self.manifest.read_bytes(), self.project.read_bytes()), original)


if __name__ == '__main__':
    unittest.main()

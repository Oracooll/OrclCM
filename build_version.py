"""Used by build_exe.bat: reads VERSION from orclcm.py, writes the Windows version
resource to build\\version_info.txt and prints the version (e.g. 1.2.000)."""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def read_version():
    with open(os.path.join(HERE, "orclcm.py"), encoding="utf-8") as f:
        m = re.search(r'^VERSION = "(\d+)\.(\d)\.(\d{3})"', f.read(), re.M)
    if not m:
        sys.exit('orclcm.py: VERSION must look like "1.X.XXX"')
    return m.group(0).split('"')[1], tuple(int(g) for g in m.groups())


def main():
    version, (major, minor, build) = read_version()
    nums = f"({major},{minor},{build},0)"
    info = (
        f"VSVersionInfo(ffi=FixedFileInfo(filevers={nums},prodvers={nums},mask=0x3f,flags=0x0,"
        "OS=0x40004,fileType=0x1,subtype=0x0,date=(0,0)),\n"
        "kids=[StringFileInfo([StringTable('040904B0',["
        "StringStruct('CompanyName','OrclCM'),"
        "StringStruct('FileDescription','OrclCM - live laptop charging wattage'),"
        f"StringStruct('FileVersion','{version}'),"
        "StringStruct('InternalName','OrclCM'),"
        f"StringStruct('OriginalFilename','OrclCM-{version}.exe'),"
        "StringStruct('ProductName','OrclCM'),"
        f"StringStruct('ProductVersion','{version}')])]),"
        "VarFileInfo([VarStruct('Translation',[1033,1200])])])\n"
    )
    os.makedirs(os.path.join(HERE, "build"), exist_ok=True)
    with open(os.path.join(HERE, "build", "version_info.txt"), "w", encoding="utf-8") as f:
        f.write(info)
    print(version)


if __name__ == "__main__":
    main()

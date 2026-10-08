# Launches OrclCM without a console window (Windows: double-click this file).
import os, runpy
runpy.run_path(os.path.join(os.path.dirname(os.path.abspath(__file__)), "orclcm.py"), run_name="__main__")

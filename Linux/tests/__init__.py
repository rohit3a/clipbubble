import os
import tempfile

# Every test gets throwaway data: point the store at a temp folder before anything imports it.
HOME = tempfile.mkdtemp(prefix="clipbubble-pytests-")
os.environ["CLIPBUBBLE_HOME"] = HOME
os.environ["CLIPBUBBLE_DOWNLOADS"] = os.path.join(HOME, "Downloads")
os.environ["CLIPBUBBLE_DEVICE_NAME"] = "Linux test"

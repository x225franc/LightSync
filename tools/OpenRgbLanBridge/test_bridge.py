import unittest,struct,base64,json
from bridge import *
class Tests(unittest.TestCase):
 def test_colors(self):
  colors=[0x0000ff,0x00ff00,0xff0000]+[0]*7
  p=struct.pack('<IH',46,10)+struct.pack('<10I',*colors)
  self.assertEqual(parse_colors(1050,p),colors)
  f=govee_frame(colors);self.assertEqual(f[6:15],bytes([255,0,0,0,255,0,0,0,255]))
  data=base64.b64decode(json.loads(lan_packet(f))['msg']['data']['pt']);self.assertEqual(__import__('functools').reduce(int.__xor__,data),0)
 def test_bounds(self):
  with self.assertRaises(ValueError):parse_colors(1050,struct.pack('<IH',6,255))
 def test_description(self):
  d=device_data([0]*10);self.assertEqual(struct.unpack_from('<I',d)[0],len(d));self.assertIn(b'Direct\0',d);self.assertTrue(d.endswith(struct.pack('<H10I',10,*([0]*10))))
if __name__ == '__main__': unittest.main()


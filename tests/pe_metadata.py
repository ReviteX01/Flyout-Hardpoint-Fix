import struct, sys, re
class PECLI:
 def __init__(self,path):
  self.d=open(path,'rb').read(); self._parse()
 def u16(self,o): return struct.unpack_from('<H',self.d,o)[0]
 def u32(self,o): return struct.unpack_from('<I',self.d,o)[0]
 def u64(self,o): return struct.unpack_from('<Q',self.d,o)[0]
 def v(self,o,n): return int.from_bytes(self.d[o:o+n],'little')
 def _parse(self):
  d=self.d; pe=self.u32(0x3c); assert d[pe:pe+4]==b'PE\0\0'; n=self.u16(pe+6); opt=pe+24; magic=self.u16(opt); dd=opt+(96 if magic==0x10b else 112); cli=self.u32(dd+14*8);secs=opt+self.u16(pe+20)
  self.sections=[]
  for i in range(n):
   o=secs+40*i; self.sections.append((self.u32(o+12),self.u32(o+8),self.u32(o+16),self.u32(o+20)))
  cor=self.tooff(cli); metadata=self.tooff(self.u32(cor+8));assert d[metadata:metadata+4]==b'BSJB'; off=metadata+16+self.u32(metadata+12); off=(off+3)&~3; self.streams={}; cnt=self.u16(off+2); off+=4
  for i in range(cnt):
   ofs=self.u32(off); sz=self.u32(off+4); nm=off+8; end=d.index(b'\x00',nm); name=d[nm:end].decode(); self.streams[name]=(metadata+ofs,sz);off=(end+4)&~3
  self.strings=self.streams['#Strings'][0]; pos=self.streams.get('#~',self.streams.get('#-'))[0]; self.heapFlags=d[pos+6]; valid=self.u64(pos+8); p=pos+24; self.rows={};
  for k in range(64):
   if (valid>>k)&1: self.rows[k]=self.u32(p);p+=4
  self.tableStart=p;self.ssz=4 if self.heapFlags&1 else 2;self.gsz=4 if self.heapFlags&2 else 2;self.bsz=4 if self.heapFlags&4 else 2
  self._offsets={};cur=p
  for k in range(64):
   if k in self.rows:
    self._offsets[k]=cur;cur+=self.rowSize(k)*self.rows[k]
  print('Streams',list(self.streams),'Rows', {k:self.rows[k] for k in [0,1,2,4,6,8,10,23,24,27,41] if k in self.rows},file=sys.stderr)
 def tooff(self,rva):
  for virt,vs,rawsz,raw in self.sections:
   if virt<=rva<virt+max(vs,rawsz):return raw+rva-virt
  raise ValueError(rva)
 def ix(self,t): return 4 if self.rows.get(t,0)>=65536 else 2
 def ci(self,tag,*tables): return 4 if max([self.rows.get(t,0) for t in tables]or[0])>=2**(16-tag) else 2
 def rowSize(self,k):
  s,g,b=self.ssz,self.gsz,self.bsz;ix=self.ix;ci=self.ci
  typeDefOrRef=ci(2,2,1,27); resolutionScope=ci(2,0,26,35,1); memberRefParent=ci(3,2,1,26,6,27)
  hasConstant=ci(2,4,8,23); hasCustomAttribute=ci(5,6,4,1,2,8,9,10,0,14,23,20,17,26,27,32,35,38,39,40,42,44,43); customAttributeType=ci(3,6,10)
  hasFieldMarshal=ci(1,4,8); hasDeclSecurity=ci(2,2,6,32); hasSemantics=ci(1,20,23); methodDefOrRef=ci(1,6,10); memberForwarded=ci(1,4,6)
  implementation=ci(2,38,35,39); typeOrMethodDef=ci(1,2,6)
  shapes={
   0:[2,s,g,g,g], 1:[resolutionScope,s,s], 2:[4,s,s,typeDefOrRef,ix(4),ix(6)],3:[ix(4)],4:[2,s,b],5:[ix(6)],6:[4,2,2,s,b,ix(8)],7:[ix(8)],8:[2,2,s],9:[ix(2),typeDefOrRef],10:[memberRefParent,s,b],11:[2,hasConstant,b],12:[hasCustomAttribute,customAttributeType,b],13:[hasFieldMarshal,b],14:[2,hasDeclSecurity,b],15:[2,4,ix(2)],16:[4,ix(4)],17:[b],18:[ix(2),ix(20)],19:[ix(20)],20:[2,s,typeDefOrRef],21:[ix(2),ix(23)],22:[ix(23)],23:[2,s,b],24:[2,ix(6),hasSemantics],25:[ix(2),methodDefOrRef,methodDefOrRef],26:[s],27:[b],28:[2,memberForwarded,s,ix(26)],29:[4,ix(4)],30:[4,4],31:[4],32:[4,2,2,2,2,4,b,s,s],33:[4],34:[4,4,4],35:[2,2,2,2,4,b,s,s,b],36:[4,ix(35)],37:[4,4,4,ix(35)],38:[4,s,b],39:[4,4,s,s,implementation],40:[4,4,s,implementation],41:[ix(2),ix(2)],42:[2,2,typeOrMethodDef,s],43:[methodDefOrRef,b],44:[ix(42),typeDefOrRef],45:[4,2,ix(2)],46:[4,2,ix(2)],47:[4,ix(2)],48:[2,2,b],49:[2,2,b],50:[4,4],51:[4,4],52:[4,4]}
  if k not in shapes:raise ValueError('Unknown table '+str(k))
  return sum(shapes[k])
 def st(self,offset):
  if not offset:return ''
  p=self.strings+offset;end=self.d.find(b'\0',p)
  return self.d[p:end].decode('utf-8','replace')
 def typeDefs(self):
  off=self._offsets[2];idx4=self.ix(4);idx6=self.ix(6);coded=self.ci(2,2,1,27); rows=[]
  for i in range(self.rows.get(2,0)):
   p=off+i*self.rowSize(2);flags=self.u32(p);name=self.st(self.v(p+4,self.ssz));nmsp=self.st(self.v(p+4+self.ssz,self.ssz));p+=4+2*self.ssz+coded;field=self.v(p,idx4);method=self.v(p+idx4,idx6);rows.append((i+1,nmsp,name,field,method))
  return rows
 def methods(self):
  off=self._offsets[6]; rows=[]; pIx=self.ix(8)
  for i in range(self.rows.get(6,0)):
   p=off+i*self.rowSize(6); rva=self.u32(p);flags=self.u16(p+6);name=self.st(self.v(p+8,self.ssz));sig=self.v(p+8+self.ssz,self.bsz); rows.append((i+1,rva,flags,name,sig))
  return rows
 def fields(self):
  off=self._offsets[4];return [(i+1,self.st(self.v(off+i*self.rowSize(4)+2,self.ssz))) for i in range(self.rows.get(4,0))]

if __name__=='__main__':
 c=PECLI(sys.argv[1]); rows=c.typeDefs(); methods=c.methods(); fields=c.fields(); pattern=re.compile(sys.argv[2] if len(sys.argv)>2 else r'(Missile|Pylon|Hardpoint|Weapon|Launcher|Mount|Rail)',re.I)
 for index,(rid,ns,name,field,method) in enumerate(rows):
  if not pattern.search(name):continue
  nextMethod=rows[index+1][4] if index+1<len(rows) else len(methods)+1; nextField=rows[index+1][3] if index+1<len(rows) else len(fields)+1
  print('\n====',rid,ns+'.'+name,'methods',method,nextMethod,'fields',field,nextField)
  for f in fields[field-1:nextField-1]:print(' F',f)
  for x in methods[method-1:nextMethod-1]:print(' M',x[:4])

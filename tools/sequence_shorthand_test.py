"""Actual UDP engineering values using shorthand in both sequence modes."""
import json,secrets,socket,time,urllib.parse,urllib.request
from integration_test import seal,wait
from runtime_load_test import packet
from sequence_test import xlsx

def run_checks(client,root,output,check):
    if client.call('/api/state')['mode']!='stopped':client.call('/api/control',dict(action='stop'))
    for code in ['A0','A1']:
        draft=client.call('/api/decoders')
        field=dict(next(row for row in draft['configuration']['Catalog']['Parameters'] if row['Code']==code))
        if code=='A0':field.update(Scale=.01,Precision=2)
        else:field.update(Enums={'0':'零状态','1':'正常状态'})
        client.call('/api/decoders/parameter',dict(id=field['Id'],revision=draft['revision'],parameter=field))
    draft=client.call('/api/decoders');source_b=dict(draft['configuration']['Packets'][0])
    source_b.update(Id='shorthand-b',Name='短引用B流',OuterSourceId=0x0202,Apid=8)
    client.call('/api/decoders/packet',dict(packet=source_b,revision=draft['revision']))
    port=json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'))['UdpPort']
    sender=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sequence=1000

    def send(value=979,enum=1,outer=0x0802):
        nonlocal sequence
        sequence+=1;raw=packet(2,0,sequence);raw[0:2]=outer.to_bytes(2,'big')
        raw[22:24]=value.to_bytes(2,'big');raw[24:26]=enum.to_bytes(2,'big')
        accepted=client.call('/api/state')['accepted'];sender.sendto(seal(raw),('127.0.0.1',port))
        wait(lambda:client.call('/api/state')['accepted']>accepted,'valid telemetry decoded')
    def preview(expression,mode):
        query=urllib.parse.urlencode(dict(filename='简写条件.xlsx',mode=mode,timeout=2,sourceIP='127.0.0.1'))
        request=urllib.request.Request(client.base+'/api/sequence/preview?'+query,
            data=xlsx([('简写'+secrets.token_hex(3),[['','参数判读','',expression,'','.2' if mode=='execute' else '','2']])]),
            headers={'Content-Type':'application/octet-stream','X-CSRF-Token':client.csrf})
        return json.loads(client.opener.open(request).read())
    def state(run):return client.call('/api/sequence/state?runId='+run)
    cases=[
        ('judge','abs(TME.A0 - 9.8) < 1'),
        ('execute','abs(TME.A0 - 9.8) < 1'),
        ('judge','TME.A0 > 9 && TME.A0.value < 10'),
        ('judge','INENUM(TME.A1)'),
        ('execute','INENUM(TME.A1.value) && abs(TME.A0 - 9.8) < 1'),
        ('judge','NOT(TME.A1 == 0) AND (TME.A0 >= 9 OR TME.A0 < 0)'),
        ('judge','abs(TME.A0.value - 9.8) < 1'),
        ('judge','abs(TME.A0 - TMB.A0) < 0.02')
    ]
    try:
        for index,(mode,expression) in enumerate(cases):
            previewed=preview(expression,mode);item=previewed['definitions'][0]
            assert not item['errors'],item['errors']
            identifier=item['definition']['Id']
            client.call('/api/sequence/apply',dict(token=previewed['token'],ids=[identifier],mode='add'))
            client.call('/api/control',dict(action='start'))
            send()
            if 'TMB.' in expression:send(980,outer=0x0202)
            cfg=client.call('/api/sequence/config')
            run=client.call('/api/sequence/control',dict(action='start',id=identifier,runName='简写实际数据'+str(index),
                revision=cfg['revision'],requestId=secrets.token_hex(16)))['runId']
            wait(lambda:state(run)['rows'][0]['status']=='等待参数','wait for new parameter data')
            check(state(run)['runtime']['running'],'cached telemetry cannot pass a shorthand/legacy step: '+expression)
            if index==0:
                send(0);check(state(run)['runtime']['running'],'a false shorthand math condition remains pending until the value changes')
            send()
            if 'TMB.' in expression:
                check(state(run)['runtime']['running'],'a fresh TME sample cannot replace the required new TMB sample')
                send(980,outer=0x0202)
            wait(lambda:not state(run)['runtime']['running'],'condition passes')
            result=state(run)
            check(result['rows'][0]['status']=='通过' and result['rows'][0]['reason']=='' and result['runtime']['abnormal']==0,
                  mode+' shorthand or legacy condition passes on actual engineering values: '+expression)
            if index==2:
                check(len(result['rows'][0]['values'])==1,'mixing bare and .value references does not create duplicate parameter bindings')
            client.call('/api/control',dict(action='stop'))
        for expression in ['TME.A0.pre0 > 0','TME.A0.raw > 0','this.value > 0','A0 > 0']:
            check(bool(preview(expression,'judge')['definitions'][0]['errors']),
                  'unavailable sequence property or incomplete reference still shows a validation error: '+expression)
    finally:sender.close()
